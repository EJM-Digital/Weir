using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>
/// A fake metadata gateway: Deluno's metadata service played by a local HTTP server. Weir asks it about a title
/// (<c>GET /metadata/search</c>), which names the poster and the original language, and then for the image
/// (<c>GET /artwork/w342/{file}</c>); the settings check asks <c>GET /health</c>. It records every request, answers only titles a test
/// taught it, and can answer "busy" the way the real service does when it is over its limit. No contract test reaches the real
/// service: a server under test is pointed here with <c>WEIR_ARTWORK_GATEWAY_URL</c> (see <see cref="Env"/>), and every other server
/// runs with it <c>off</c>.
/// <code>
/// using var gateway = new FakeGateway();
/// gateway.Knows("nosferatu", "nosferatu.jpg", tmdbId: 653, originalLanguage: "de");
/// await WeirServer.StartNewAsync(gateway.Env);
/// </code>
/// </summary>
public sealed class FakeGateway : FakeHttpServer
{
    /// <summary>The bytes served for every poster image: a JPEG header and a few more bytes.</summary>
    public static readonly byte[] ImageBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5];

    private readonly object _gate = new();
    private readonly Dictionary<string, Title> _titles = new();
    private readonly Dictionary<int, Title> _byId = new();
    private readonly HashSet<string> _images = [];
    private readonly TaskCompletionSource _searchHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _busyRetryAfterSeconds;
    private bool _busy;
    private TaskCompletionSource? _searchesHeldBack;

    /// <summary>What a server needs to use this gateway: pass it to <see cref="WeirServer.StartNewAsync"/>.</summary>
    public IReadOnlyDictionary<string, string> Env => new Dictionary<string, string> { ["WEIR_ARTWORK_GATEWAY_URL"] = BaseUrl };

    /// <summary>Answers a search for <paramref name="title"/> (any letter case) with one result whose poster is <paramref name="posterFile"/>.</summary>
    public void Knows(string title, string posterFile, int tmdbId = 1, string originalLanguage = "en")
    {
        var known = new Title(posterFile, tmdbId, originalLanguage);
        lock (_gate)
        {
            _titles[title.ToLowerInvariant()] = known;
            _byId[tmdbId] = known;
            _images.Add(posterFile);
        }
    }

    /// <summary>Serves <paramref name="posterFile"/> at the artwork route, as the real gateway serves any TMDb image.</summary>
    public void ServesImage(string posterFile)
    {
        lock (_gate)
        {
            _images.Add(posterFile);
        }
    }

    /// <summary>Answers every search with 503 <c>provider_busy</c> and this <c>Retry-After</c> (none when null).</summary>
    public void Busy(int? retryAfterSeconds = 600)
    {
        lock (_gate)
        {
            _busy = true;
            _busyRetryAfterSeconds = retryAfterSeconds;
        }
    }

    /// <summary>
    /// Keeps every search waiting for its answer, the way a slow service does, until <see cref="ReleaseSearches"/>. A held search is not
    /// in <see cref="FakeHttpServer.Requests"/> until it is answered; <see cref="SearchHeldAsync"/> says when one has arrived.
    /// </summary>
    public void HoldSearches()
    {
        lock (_gate)
        {
            _searchesHeldBack = new TaskCompletionSource();
        }
    }

    public void ReleaseSearches()
    {
        lock (_gate)
        {
            _searchesHeldBack?.TrySetResult();
        }
    }

    /// <summary>Completes when a search is waiting for its answer. Call it after <see cref="HoldSearches"/>.</summary>
    public Task SearchHeldAsync() => _searchHeld.Task;

    public override void Dispose()
    {
        ReleaseSearches();
        base.Dispose();
    }

    public void Calm()
    {
        lock (_gate)
        {
            _busy = false;
        }
    }

    public IReadOnlyList<RecordedRequest> Searches() => Requests.Where(request => request.Path == "/metadata/search").ToList();

    public IReadOnlyList<RecordedRequest> SearchesFor(string title) => Searches()
        .Where(request => request.Query.TryGetValue("query", out var values) && string.Equals(values[0], title, StringComparison.OrdinalIgnoreCase))
        .ToList();

    public IReadOnlyList<RecordedRequest> ImageRequests() =>
        Requests.Where(request => request.Path.StartsWith("/artwork/", StringComparison.Ordinal)).ToList();

    protected override HttpAnswer Answer(RecordedRequest request)
    {
        if (request.Method != "GET")
        {
            return Json(404, new JsonObject { ["error"] = "not_found" });
        }

        if (request.Path == "/health")
        {
            return Json(200, new JsonObject { ["service"] = "deluno-metadata-gateway", ["status"] = "ok" });
        }

        if (request.Path == "/metadata/search")
        {
            return AnswerSearch(request);
        }

        const string ImagePrefix = "/artwork/w342/";
        if (request.Path.StartsWith(ImagePrefix, StringComparison.Ordinal))
        {
            var file = request.Path[(request.Path.LastIndexOf('/') + 1)..];
            lock (_gate)
            {
                if (_images.Contains(file))
                {
                    return new HttpAnswer(200, ImageBytes, "image/jpeg");
                }
            }
        }

        return Json(404, new JsonObject { ["error"] = "not_found" });
    }

    private HttpAnswer AnswerSearch(RecordedRequest request)
    {
        Task? held;
        lock (_gate)
        {
            held = _searchesHeldBack?.Task;
        }

        if (held is { IsCompleted: false })
        {
            _searchHeld.TrySetResult();
            held.Wait();
        }

        var query = request.Query.TryGetValue("query", out var queries) ? queries[0] : string.Empty;
        Title? found;
        lock (_gate)
        {
            if (_busy)
            {
                var headers = _busyRetryAfterSeconds is { } seconds
                    ? new Dictionary<string, string> { ["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture) }
                    : null;
                return Json(503, new JsonObject { ["error"] = "provider_busy" }, headers);
            }

            var byId = request.Query.TryGetValue("providerId", out var ids) ? ids[0] : string.Empty;
            found = byId.Length > 0 && byId.All(char.IsAsciiDigit)
                ? (int.TryParse(byId, CultureInfo.InvariantCulture, out var id) ? _byId.GetValueOrDefault(id) : null)
                : _titles.GetValueOrDefault(query.ToLowerInvariant());
        }

        var results = new JsonArray();
        if (found is not null)
        {
            results.Add(new JsonObject
            {
                ["provider"] = "tmdb",
                ["providerId"] = found.TmdbId.ToString(CultureInfo.InvariantCulture),
                ["title"] = query,
                ["originalLanguage"] = found.OriginalLanguage,
                ["posterUrl"] = $"{BaseUrl}/artwork/w780/{found.PosterFile}",
            });
        }

        return Json(200, new JsonObject { ["provider"] = "deluno-broker", ["resultCount"] = results.Count, ["results"] = results });
    }

    private static HttpAnswer Json(int status, JsonNode body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json", headers);

    private sealed record Title(string PosterFile, int TmdbId, string OriginalLanguage);
}
