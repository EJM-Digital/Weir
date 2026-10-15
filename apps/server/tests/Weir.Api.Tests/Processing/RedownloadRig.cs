using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Tests.MediaManagers;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// A running Weir with one library per kind, a scripted Sonarr or Radarr connected to it, and a scanned file the library
/// remembers as matched to a title at that manager: everything "Download again" needs, so a test only scripts the manager.
/// </summary>
internal sealed class RedownloadRig : IAsyncDisposable
{
    public const string FilmPath = "/library/films/Arrival (2016)/Arrival.mkv";
    public const string RadarrName = "Radarr on 192.0.2.20";
    public const string SonarrName = "Sonarr on 192.0.2.30";
    public const string EpisodePath = "/library/tv/Severance/Season 01/Severance - S01E01.mkv";
    public const string OtherEpisodePath = "/library/tv/Severance/Season 01/Severance - S01E02.mkv";

    private const string RadarrUrl = "http://192.0.2.20:7878";
    private const string SonarrUrl = "http://192.0.2.30:8989";

    private RedownloadRig(WeirTestServer server, ApiTestClient admin, ScriptedManager manager)
    {
        Server = server;
        Admin = admin;
        Manager = manager;
    }

    public WeirTestServer Server { get; }

    /// <summary>Signed in as an administrator.</summary>
    public ApiTestClient Admin { get; }

    public ScriptedManager Manager { get; }

    public static async Task<RedownloadRig> StartAsync()
    {
        var manager = new ScriptedManager();
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<IManagerHttpHandlerFactory>(manager));
        await TestDatabase.SeedAdminAsync(server);
        var admin = new ApiTestClient(server);
        await admin.SignInAsync();
        return new RedownloadRig(server, admin, manager);
    }

    public static string RedownloadsPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-redownloads";

    /// <summary>A movie library whose one scanned file is matched to movie 7 at a connected Radarr; the library and connection ids come back.</summary>
    public async Task<(long LibraryId, long ConnectionId)> AddRadarrFilmAsync() =>
        await AddMatchedFileAsync("Films", "movie", "radarr", RadarrUrl, FilmPath, "Arrival", ArrRedownloadReplies.MovieId);

    /// <summary>A TV library whose scanned episodes are matched to series 3 at a connected Sonarr; the library and connection ids come back.</summary>
    public async Task<(long LibraryId, long ConnectionId)> AddSonarrEpisodeAsync()
    {
        var added = await AddMatchedFileAsync("Shows", "tv", "sonarr", SonarrUrl, EpisodePath, "Severance", ArrRedownloadReplies.SeriesId);
        await InsertFileAsync(added.LibraryId, OtherEpisodePath, "sonarr", "Severance", added.ConnectionId, ArrRedownloadReplies.SeriesId);
        return added;
    }

    /// <summary>A library with a scanned file and no manager link at all.</summary>
    public async Task<long> AddUnlinkedFilmAsync()
    {
        var libraryId = await InsertLibraryAsync("Unlinked films", "movie");
        await InsertFileAsync(libraryId, FilmPath, kind: null, title: null, connectionId: null, titleId: null);
        return libraryId;
    }

    /// <summary>A movie library whose scanned file the scan matched to a title at a manager of <paramref name="kind"/>; null ids are a scan that did not resolve the title.</summary>
    public async Task<long> AddLinkedFilmAsync(string kind, long? connectionId, long? titleId)
    {
        var libraryId = await InsertLibraryAsync("Linked films", "movie");
        await InsertFileAsync(libraryId, FilmPath, kind, "Arrival", connectionId, titleId);
        return libraryId;
    }

    public async Task<long> ConnectAsync(string kind, string baseUrl)
    {
        using var response = await Admin.PostAsync(
            "/api/v1/media-managers/connections",
            new { csrf_token = await Admin.CsrfAsync(), kind, base_url = baseUrl, api_key = "the-managers-api-key" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response))["id"]!.GetValue<long>();
    }

    public Task RecordRemovedTrackAsync(long libraryId, string path, string language) =>
        TestDatabase.ExecuteAsync(
            Server,
            "INSERT INTO removed_tracks (library_id, relative_path, language, track_type, codec, reason) " +
            "VALUES ($library, $path, $language, 'audio', 'eac3', 'Not a language your rules keep')",
            ("$library", libraryId),
            ("$path", path),
            ("$language", language));

    /// <summary>The request the web sends: the file's path and the confirmation, with a CSRF token.</summary>
    public async Task<HttpResponseMessage> RedownloadAsync(ApiTestClient client, long libraryId, string path, bool confirm = true) =>
        await client.PostAsync(RedownloadsPath(libraryId), new { path, confirm_destructive = confirm, csrf_token = await client.CsrfAsync() });

    /// <summary>Signs a second user in as a viewer.</summary>
    public async Task<ApiTestClient> ViewerAsync()
    {
        await TestDatabase.SeedViewerAsync(Server);
        var viewer = new ApiTestClient(Server);
        await viewer.SignInAsync("bob", ViewerPassword);
        return viewer;
    }

    /// <summary>Signs a third user in as an operator.</summary>
    public async Task<ApiTestClient> OperatorAsync()
    {
        await TestDatabase.SeedUserAsync(Server, "carol", ViewerPassword, "operator");
        var operatorClient = new ApiTestClient(Server);
        await operatorClient.SignInAsync("carol", ViewerPassword);
        return operatorClient;
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();

    private async Task<(long LibraryId, long ConnectionId)> AddMatchedFileAsync(
        string name, string mediaType, string kind, string baseUrl, string path, string title, long titleId)
    {
        var connectionId = await ConnectAsync(kind, baseUrl);
        var libraryId = await InsertLibraryAsync(name, mediaType);
        await InsertFileAsync(libraryId, path, kind, title, connectionId, titleId);
        return (libraryId, connectionId);
    }

    private Task<long> InsertLibraryAsync(string name, string mediaType) =>
        TestDatabase.ScalarAsync(
            Server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ($name, $type, '/in', '/out', '/work', 9) RETURNING id",
            ("$name", name),
            ("$type", mediaType));

    private Task InsertFileAsync(long libraryId, string path, string? kind, string? title, long? connectionId, long? titleId) =>
        TestDatabase.ExecuteAsync(
            Server,
            "INSERT INTO library_files (library_id, path, size_bytes, mtime, classification, removed_audio_tracks, removed_subtitle_tracks, " +
            "estimated_bytes_saved, manager_kind, manager_title, manager_connection_id, manager_title_id) " +
            "VALUES ($library, $path, 1000, 1700000000, 'matches', 0, 0, 0, $kind, $title, $connection, $titleId)",
            ("$library", libraryId),
            ("$path", path),
            ("$kind", (object?)kind ?? DBNull.Value),
            ("$title", (object?)title ?? DBNull.Value),
            ("$connection", (object?)connectionId ?? DBNull.Value),
            ("$titleId", titleId is { } id ? (object)id.ToString(CultureInfo.InvariantCulture) : DBNull.Value));
}
