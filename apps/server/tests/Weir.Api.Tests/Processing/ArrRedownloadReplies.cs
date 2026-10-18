using System.Globalization;
using System.Text.Json.Nodes;
using Weir.Tests.Shared;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// What the scripted Sonarr and Radarr answer in the "Download again" tests, named in this one file. Every reply is one a real Sonarr
/// or Radarr gave (<c>fixtures/recorded/sonarr</c> and <c>radarr</c>, recorded by <c>apps/server/tools/Weir.ArrRecorder</c>): the
/// movie with its file, the movie after its file was deleted, the series' episode files, the answer to a search command and a
/// manager that fails on its own. Only the paths of the episode files change (<see cref="EpisodeFiles"/>), because a test chooses
/// which files the library holds; the ids, sizes and every other member are the manager's.
/// </summary>
internal static class ArrRedownloadReplies
{
    private static readonly ArrRecording MovieRedownload = ArrRecording.Load("radarr", "redownload");
    private static readonly ArrRecording MovieAfterDelete = ArrRecording.Load("radarr", "redownload-file-gone");
    private static readonly ArrRecording SeriesRedownload = ArrRecording.Load("sonarr", "redownload");
    private static readonly ArrRecording ManagerFailure = ArrRecording.Load("sonarr", "downloaded-scan-refuses-a-number");

    private static readonly ArrExchange MovieRead = MovieRedownload.Exchanges.Single(exchange => exchange.Request.Method == "GET");
    private static readonly ArrExchange MovieFileDelete = MovieRedownload.Exchanges.Single(exchange => exchange.Request.Method == "DELETE");
    private static readonly ArrExchange EpisodeFileList = SeriesRedownload.Exchanges.Single(exchange => exchange.Request.Method == "GET");
    private static readonly ArrExchange EpisodeFileDelete = SeriesRedownload.Exchanges.Single(exchange => exchange.Request.Method == "DELETE");

    public static readonly long MovieId = LastSegment(MovieRead.Request.Path);
    public static readonly long MovieFileId = LastSegment(MovieFileDelete.Request.Path);
    public static readonly long MovieFileBytes = (long)MovieRead.Reply.Body!["movieFile"]!["size"]!;
    public static readonly long SeriesId = long.Parse(EpisodeFileList.Request.Query["seriesId=".Length..], CultureInfo.InvariantCulture);
    public static readonly long EpisodeFileId = LastSegment(EpisodeFileDelete.Request.Path);
    public static readonly long OtherEpisodeFileId = EpisodeFileList.Reply.Body!.AsArray().Select(file => (long)file!["id"]!).First(id => id != EpisodeFileId);

    /// <summary>Radarr's answer for a movie that has a file.</summary>
    public static readonly string MovieWithFile = MovieRead.Reply.Text;

    /// <summary>Radarr's answer for the same movie once its file is gone.</summary>
    public static readonly string MovieWithoutFile = MovieAfterDelete.Exchanges[0].Reply.Text;

    /// <summary>Radarr's answer to the search it was asked for.</summary>
    public static readonly string CommandAccepted = MovieRedownload.Exchanges.Single(exchange => exchange.Request.Method == "POST").Reply.Text;

    /// <summary>What a Sonarr that fails on its own answers: a 500 with its own message.</summary>
    public static readonly string ManagerError = ManagerFailure.Only.Reply.Text;

    /// <summary>
    /// Sonarr's episode files for the series, two of them: the one that "Download again" deletes and another. Each keeps the manager's
    /// own id, size and members; only the path is the one the test's library holds.
    /// </summary>
    public static string EpisodeFiles(string firstPath, string secondPath)
    {
        var files = EpisodeFileList.Reply.Body!.AsArray().Select(file => file!.DeepClone().AsObject()).ToList();
        var first = files.Single(file => (long)file["id"]! == EpisodeFileId);
        var second = files.First(file => (long)file["id"]! == OtherEpisodeFileId);
        first["path"] = firstPath;
        second["path"] = secondPath;
        return new JsonArray(first, second).ToJsonString();
    }

    private static long LastSegment(string path) => long.Parse(path[(path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
}
