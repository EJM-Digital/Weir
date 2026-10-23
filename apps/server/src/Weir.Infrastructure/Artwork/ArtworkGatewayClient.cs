using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Weir.Core;
using Weir.Core.Artwork;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Artwork;

/// <summary>
/// Asks Deluno's metadata service about a title: one request to find it, which also says its original language, and one for the poster
/// image. It only reads, and sends only the title and year, or the ids a media manager gave. Every failure is an answer, never an
/// exception.
/// </summary>
public sealed class ArtworkGatewayClient
{
    private const string FilmMediaType = "movies";
    private const string SeriesMediaType = "tv";
    private const string SeriesScope = "tv";
    private const string HealthyStatus = "ok";
    private const int MaxSearchBytes = 1024 * 1024;
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const int ReadChunkBytes = 16 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly string _baseUrl;
    private readonly string _host;
    private readonly ManagerAddressPolicy _gatewayPolicy;
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly TimeProvider _time;
    private readonly ILogger<ArtworkGatewayClient> _logger;

    public ArtworkGatewayClient(WeirOptions options, IManagerHttpHandlerFactory handlers, TimeProvider time, ILogger<ArtworkGatewayClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _baseUrl = options.ArtworkGatewayUrl;
        _host = _baseUrl.Length > 0 ? new Uri(_baseUrl).Host : string.Empty;
        // The real service is a public address and must resolve to one. An address someone configured by hand is theirs to
        // point anywhere, a stand-in on this machine included.
        _gatewayPolicy = _baseUrl.Equals(ArtworkPosterSource.DefaultGatewayUrl, StringComparison.OrdinalIgnoreCase) ? ManagerAddressPolicy.Public : ManagerAddressPolicy.Local;
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _handlers = handlers;
        _time = time;
        _logger = logger;
    }

    /// <summary>Whether a gateway is configured. With none (<c>WEIR_ARTWORK_GATEWAY_URL=off</c>) no lookup is made.</summary>
    public bool IsConfigured => _baseUrl.Length > 0;

    /// <summary>
    /// Find a title's poster. A TMDb id is looked up exactly; a series with only a TheTVDB id is looked up there; anything else is
    /// searched for by title and year.
    /// </summary>
    public Task<GatewayAnswer<GatewayMatch>> FindAsync(ArtworkLookup lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        return lookup.TmdbId is null && lookup.TvdbId is { } tvdbId && lookup.MediaScope == SeriesScope
            ? FindSeriesAsync(tvdbId, cancellationToken)
            : SearchAsync(lookup, cancellationToken);
    }

    /// <summary>Whether the service answers its health check, which is not a metadata lookup and costs none of the lookup budget.</summary>
    public async Task<GatewayStatus> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var answer = await GetAsync($"{_baseUrl}/health", MaxSearchBytes, _gatewayPolicy, cancellationToken).ConfigureAwait(false);
        if (answer.Status != GatewayStatus.Ok)
        {
            return answer.Status;
        }

        return ParseObject(answer.Value!.Bytes) is { } body && Text(body.Get("status")) == HealthyStatus ? GatewayStatus.Ok : GatewayStatus.Unavailable;
    }

    /// <summary>The poster image, at Weir's size when it comes from TMDb.</summary>
    public async Task<GatewayAnswer<GatewayBody>> FetchPosterAsync(string posterRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(posterRef);
        var answer = await GetAsync(ArtworkPosterSource.ImageUrl(_baseUrl, posterRef), MaxImageBytes, ImagePolicy(posterRef), cancellationToken).ConfigureAwait(false);
        return answer.Status == GatewayStatus.Ok && !answer.Value!.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? GatewayAnswers.NotFound<GatewayBody>()
            : answer;
    }

    private async Task<GatewayAnswer<GatewayMatch>> SearchAsync(ArtworkLookup lookup, CancellationToken cancellationToken)
    {
        var query = new List<string>
        {
            "mediaType=" + (lookup.MediaScope == SeriesScope ? SeriesMediaType : FilmMediaType),
            "query=" + Uri.EscapeDataString(lookup.Title.Length > 0 ? lookup.Title : Digits(lookup.TmdbId)),
        };
        if (lookup.Year is { } year)
        {
            query.Add("year=" + Digits(year));
        }

        if (lookup.TmdbId is { } tmdbId)
        {
            query.Add("providerId=" + Digits(tmdbId));
        }

        var answer = await GetAsync($"{_baseUrl}/metadata/search?{string.Join('&', query)}", MaxSearchBytes, _gatewayPolicy, cancellationToken).ConfigureAwait(false);
        return Read(answer, ReadSearchMatch);
    }

    private async Task<GatewayAnswer<GatewayMatch>> FindSeriesAsync(long tvdbId, CancellationToken cancellationToken)
    {
        var answer = await GetAsync($"{_baseUrl}/metadata/tvdb/tv/{Digits(tvdbId)}", MaxSearchBytes, _gatewayPolicy, cancellationToken).ConfigureAwait(false);
        return Read(answer, ReadSeriesMatch);
    }

    private GatewayAnswer<GatewayMatch> Read(GatewayAnswer<GatewayBody> answer, Func<WireObject, GatewayMatch?> match)
    {
        if (answer.Status != GatewayStatus.Ok)
        {
            return new GatewayAnswer<GatewayMatch>(answer.Status, RetryAfter: answer.RetryAfter);
        }

        return ParseObject(answer.Value!.Bytes) is { } body && match(body) is { } found ? GatewayAnswers.Of(found) : GatewayAnswers.NotFound<GatewayMatch>();
    }

    private ManagerAddressPolicy ImagePolicy(string posterRef) =>
        posterRef.StartsWith("https://", StringComparison.Ordinal) ? ManagerAddressPolicy.Public : _gatewayPolicy;

    private async Task<GatewayAnswer<GatewayBody>> GetAsync(string url, int maxBytes, ManagerAddressPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient(_handlers.Handler(followRedirects: false, policy), disposeHandler: false) { Timeout = RequestTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Weir", WeirVersion.BuildVersion.Split('+')[0]));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Classify(response);
            }

            var bytes = response.Content.Headers.ContentLength > maxBytes
                ? null
                : await ReadAtMostAsync(response.Content, maxBytes, cancellationToken).ConfigureAwait(false);
            return bytes is null
                ? GatewayAnswers.NotFound<GatewayBody>()
                : GatewayAnswers.Of(new GatewayBody(bytes, response.Content.Headers.ContentType?.MediaType ?? string.Empty));
        }
        catch (Exception exception) when (MediaManagerHttpClient.IsTransportFailure(exception, cancellationToken) || exception is UriFormatException)
        {
            _logger.LogWarning("Weir could not reach the metadata service for posters ({Reason}).", MediaManagerHttpClient.ClassifyTransportFailure(exception));
            return GatewayAnswers.Unavailable<GatewayBody>();
        }
    }

    /// <summary>A busy answer says how long to wait, other refusals say no, and an error of the service's own is a reason to try again later.</summary>
    private GatewayAnswer<GatewayBody> Classify(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests => GatewayAnswers.Busy<GatewayBody>(RetryAfter(response)),
        >= HttpStatusCode.InternalServerError => GatewayAnswers.Unavailable<GatewayBody>(),
        _ => GatewayAnswers.NotFound<GatewayBody>(),
    };

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        return header?.Delta ?? (header?.Date is { } date ? date - _time.GetUtcNow() : null);
    }

    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[ReadChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private WireObject? ParseObject(byte[] body)
    {
        try
        {
            return WireJsonParser.ParseBytes(body) as WireObject;
        }
        catch (WireJsonDecodeException exception)
        {
            _logger.LogWarning(exception, "The metadata service answered with something Weir could not read.");
            return null;
        }
    }

    /// <summary>The first result that has a poster, or the first result when none has. The service ranks its results, so the first is its best match.</summary>
    private GatewayMatch? ReadSearchMatch(WireObject answer)
    {
        if (answer.Get("results") is not WireArray results)
        {
            return null;
        }

        var matches = results.Items.OfType<WireObject>().Select(MatchOf).ToList();
        return matches.Find(match => match.PosterRef is not null) ?? matches.FirstOrDefault();
    }

    private GatewayMatch? ReadSeriesMatch(WireObject answer) => answer.Get("series") is WireObject series ? MatchOf(series) : null;

    private GatewayMatch MatchOf(WireObject record)
    {
        var posterRef = ArtworkPosterSource.RefFromAnswerUrl(Text(record.Get("posterUrl")), _host);
        var tmdbId = long.TryParse(Text(record.Get("tmdbId")) ?? Text(record.Get("providerId")), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (long?)null;
        var language = WireStrings.Strip(Text(record.Get("originalLanguage")) ?? string.Empty).ToLowerInvariant();
        return new GatewayMatch(posterRef, tmdbId, language.Length > 0 ? language : null);
    }

    private static string? Text(WireValue? value) => value is WireString text ? text.Value : null;

    private static string Digits(long? number) => number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
