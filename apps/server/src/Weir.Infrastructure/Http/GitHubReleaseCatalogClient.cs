using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.Updates;

namespace Weir.Infrastructure.Http;

/// <summary>
/// Finds the newest release on GitHub without spending its unauthenticated API allowance, which every Weir, tray and other
/// GitHub client on a network shares: the public release feed answers, and so do the download addresses, as neither counts
/// against it. Only when the feed cannot be read is the API asked. When GitHub then limits the network (403 or 429 with no
/// allowance left, or a <c>Retry-After</c>) the API is left alone until it says the limit lifts. A conditional request would
/// not help: GitHub counts an unchanged answer (a 304) against the allowance too.
/// </summary>
public sealed class GitHubReleaseCatalogClient : IReleaseCatalogClient
{
    private const long MostFeedBytes = 2_000_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeastWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MostWait = TimeSpan.FromHours(1);
    private static readonly long MostUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private readonly TimeProvider _time;
    private readonly ILogger<GitHubReleaseCatalogClient> _logger;
    private readonly HttpMessageHandler _handler;
    private readonly string _feedUrl;
    private readonly Lock _gate = new();
    private IReadOnlyList<GitHubReleaseRecord>? _lastKnown;
    private (int Status, DateTimeOffset ResetsAt)? _limit;

    /// <summary>
    /// The <paramref name="handler"/> carries the requests; the default connects to loopback first and follows redirects. A test names the
    /// <paramref name="feedUrl"/> of a feed to play instead of GitHub's public one.
    /// </summary>
    public GitHubReleaseCatalogClient(TimeProvider time, ILogger<GitHubReleaseCatalogClient> logger, HttpMessageHandler? handler = null, string? feedUrl = null)
    {
        _feedUrl = feedUrl ?? ReleaseCatalog.ReleasesFeedUrl;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _handler = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectCallback = LoopbackFirstConnect.ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
    }

    public async Task<GitHubReleaseRecord?> FetchLatestAsync(string currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            var fromFeed = await ReadFeedAsync(currentVersion, cancellationToken).ConfigureAwait(false);
            Remember(fromFeed);
            return ReleaseSelection.NewestFor(fromFeed, currentVersion);
        }
        catch (Exception exception) when (exception is HttpRequestException or XmlException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug(exception, "The public release feed could not be read, so GitHub's API is asked for the release list.");
        }

        return await ReadApiAsync(currentVersion, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(string url, string currentVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", $"Weir/{currentVersion}");
        return request;
    }

    private async Task<IReadOnlyList<GitHubReleaseRecord>> ReadFeedAsync(string currentVersion, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_handler, disposeHandler: false) { Timeout = RequestTimeout, MaxResponseContentBufferSize = MostFeedBytes };
        using var request = Request(_feedUrl, currentVersion);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ReleaseFeed.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The newest release from the API, which the feed could not give. A limit is a <see cref="ReleaseFetchException"/> that
    /// says when it lifts, with the newest release Weir last knew; until then the API is not asked again.
    /// </summary>
    private async Task<GitHubReleaseRecord?> ReadApiAsync(string currentVersion, CancellationToken cancellationToken)
    {
        var (known, limit) = Known();
        if (limit is { } active && active.ResetsAt > _time.GetUtcNow())
        {
            throw Limited(active.Status, active.ResetsAt, known, currentVersion);
        }

        using var client = new HttpClient(_handler, disposeHandler: false) { Timeout = RequestTimeout };
        using var request = Request(ReleaseCatalog.ReleasesUrl, currentVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var status = (int)response.StatusCode;
        if (status >= 400)
        {
            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) || ResetOf(response) is not { } resetsAt)
            {
                throw new ReleaseFetchException(status);
            }

            lock (_gate)
            {
                _limit = (status, resetsAt);
            }

            throw Limited(status, resetsAt, known, currentVersion);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var fetched = ReleaseCatalog.CoerceReleaseListPayload(WireJsonParser.ParseBytes(bytes));
        Remember(fetched);
        lock (_gate)
        {
            _limit = null;
        }

        return ReleaseSelection.NewestFor(fetched, currentVersion);
    }

    private static ReleaseFetchException Limited(int status, DateTimeOffset resetsAt, IReadOnlyList<GitHubReleaseRecord>? known, string currentVersion) =>
        new(status, new ReleaseRateLimit(resetsAt, known is null ? null : ReleaseSelection.NewestFor(known, currentVersion)));

    /// <summary>
    /// When GitHub says the limit lifts: <c>Retry-After</c> when sent, otherwise <c>X-RateLimit-Reset</c> once no allowance is
    /// left; null when the response is not a limit. Both are worked out as a wait from GitHub's own <c>Date</c>, so a clock
    /// that is off here changes nothing, and kept between a minute and an hour from now, so an odd value never makes Weir
    /// hammer GitHub or give up on it.
    /// </summary>
    private DateTimeOffset? ResetOf(HttpResponseMessage response)
    {
        var now = _time.GetUtcNow();
        var theirNow = response.Headers.Date ?? now;
        var wait = RetryAfterWait(response, theirNow) ?? ResetWait(response, theirNow);
        return wait is { } until ? now + (until < LeastWait ? LeastWait : until > MostWait ? MostWait : until) : null;
    }

    /// <summary>How long <c>Retry-After</c> asks to wait; a date in it is measured from GitHub's own clock, as sent in <c>Date</c>.</summary>
    private static TimeSpan? RetryAfterWait(HttpResponseMessage response, DateTimeOffset theirNow) =>
        response.Headers.RetryAfter is { } after ? after.Delta ?? (after.Date - theirNow) : null;

    /// <summary>How long until <c>X-RateLimit-Reset</c> once no allowance is left; null for a missing or impossible value.</summary>
    private static TimeSpan? ResetWait(HttpResponseMessage response, DateTimeOffset theirNow) =>
        HeaderOf(response, "X-RateLimit-Remaining") == "0"
        && long.TryParse(HeaderOf(response, "X-RateLimit-Reset"), CultureInfo.InvariantCulture, out var epoch)
        && epoch is >= 0 && epoch <= MostUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(epoch) - theirNow
            : null;

    private static string? HeaderOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private (IReadOnlyList<GitHubReleaseRecord>? Releases, (int Status, DateTimeOffset ResetsAt)? Limit) Known()
    {
        lock (_gate)
        {
            return (_lastKnown, _limit);
        }
    }

    private void Remember(IReadOnlyList<GitHubReleaseRecord> releases)
    {
        lock (_gate)
        {
            _lastKnown = releases;
        }
    }
}
