using System.Net;
using Weir.Api.Tests.MediaManagers;

namespace Weir.Api.Tests.Processing;

/// <summary>How the scripted Sonarr and Radarr answer, and how a test reads what they were asked.</summary>
internal static class RedownloadScripts
{
    public static readonly string[] RadarrDeleteThenSearch =
    [
        $"GET /api/v3/movie/{ArrRedownloadReplies.MovieId}",
        $"DELETE /api/v3/moviefile/{ArrRedownloadReplies.MovieFileId}",
        "POST /api/v3/command",
    ];

    public static readonly string[] SonarrDeleteThenSearch =
    [
        $"GET /api/v3/episodefile?seriesId={ArrRedownloadReplies.SeriesId}",
        $"DELETE /api/v3/episodefile/{ArrRedownloadReplies.EpisodeFileId}",
        "POST /api/v3/command",
    ];

    public static string MoviePath => $"/api/v3/movie/{ArrRedownloadReplies.MovieId}";

    public static string MovieFilePath => $"/api/v3/moviefile/{ArrRedownloadReplies.MovieFileId}";

    public static string EpisodeFilePath => $"/api/v3/episodefile/{ArrRedownloadReplies.EpisodeFileId}";

    /// <summary>A Radarr that has the movie with its file, deletes it and accepts the search.</summary>
    public static ScriptedManager RadarrThatDeletesAndSearches(this ScriptedManager manager) => manager
        .Json(HttpMethod.Get, MoviePath, ArrRedownloadReplies.MovieWithFile)
        .Deleting(MovieFilePath)
        .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.CommandAccepted, HttpStatusCode.Created);

    /// <summary>A Sonarr that lists the series' episode files, deletes the first and accepts the search.</summary>
    public static ScriptedManager SonarrThatDeletesAndSearches(this ScriptedManager manager) => manager
        .Json(HttpMethod.Get, "/api/v3/episodefile", ArrRedownloadReplies.EpisodeFiles(RedownloadRig.EpisodePath, RedownloadRig.OtherEpisodePath))
        .Deleting(EpisodeFilePath)
        .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.CommandAccepted, HttpStatusCode.Created);

    /// <summary>A DELETE the manager answers with an empty success.</summary>
    public static ScriptedManager Deleting(this ScriptedManager manager, string path) =>
        manager.Route(HttpMethod.Delete, path, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

    /// <summary>A request the manager cannot answer at all, as when it is switched off or unplugged.</summary>
    public static ScriptedManager Unreachable(this ScriptedManager manager, HttpMethod method, string path) =>
        manager.Route(method, path, _ => throw new HttpRequestException("No connection could be made because the target machine actively refused it."));

    /// <summary>"METHOD /path?query" for each request the manager received, in order.</summary>
    public static string[] Asked(this ScriptedManager manager) =>
        [.. manager.Calls.Select(call => $"{call.Method} {call.PathAndQuery}")];

    /// <summary>The body of the one search the manager was asked for.</summary>
    public static string SearchBody(this ScriptedManager manager) =>
        manager.Calls.Single(call => call.Method == "POST" && call.PathAndQuery == "/api/v3/command").Body;
}
