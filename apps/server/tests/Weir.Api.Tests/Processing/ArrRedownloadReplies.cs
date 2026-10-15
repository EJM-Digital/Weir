namespace Weir.Api.Tests.Processing;

/// <summary>
/// What the fake Sonarr and Radarr answer in the "Download again" tests, named in this one file.
/// <para>Every reply here is an ASSUMED SHAPE, not a recording: no real Sonarr or Radarr has been recorded yet. Each one
/// carries only the members Weir reads (<c>ArrManagerRedownload</c> reads the movie's <c>movieFile.id</c> and <c>size</c>, and
/// the episode files' <c>id</c>, <c>size</c> and <c>path</c>) and is to be replaced by a recorded reply when the Sonarr
/// recording (#1013) and the Radarr recording (#1014) land. Until then the tests prove what Weir sends and what it tells the
/// person, not what the real products answer.</para>
/// </summary>
internal static class ArrRedownloadReplies
{
    public const long MovieId = 7;
    public const long MovieFileId = 42;
    public const long MovieFileBytes = 1_500_000_000;
    public const long SeriesId = 3;
    public const long EpisodeFileId = 55;
    public const long OtherEpisodeFileId = 56;

    /// <summary>Assumed shape (replaced by the Radarr recording, #1014): a movie that has a file.</summary>
    public static readonly string MovieWithFile =
        $$$"""{"id":{{{MovieId}}},"title":"Arrival","hasFile":true,"movieFile":{"id":{{{MovieFileId}}},"size":{{{MovieFileBytes}}},"path":"/movies/Arrival (2016)/Arrival.mkv"}}""";

    /// <summary>Assumed shape (replaced by the Radarr recording, #1014): a movie whose file is already gone.</summary>
    public static readonly string MovieWithoutFile = $$$"""{"id":{{{MovieId}}},"title":"Arrival","hasFile":false}""";

    /// <summary>Assumed shape (replaced by the Sonarr recording, #1013): a series' episode files, two of them.</summary>
    public static string EpisodeFiles(string firstPath, string secondPath) =>
        $$$"""[{"id":{{{EpisodeFileId}}},"seriesId":{{{SeriesId}}},"size":900000000,"path":"{{{firstPath}}}"},{"id":{{{OtherEpisodeFileId}}},"seriesId":{{{SeriesId}}},"size":123,"path":"{{{secondPath}}}"}]""";

    /// <summary>Assumed shape (replaced by the Sonarr and Radarr recordings, #1013 and #1014): the answer to a command.</summary>
    public const string CommandAccepted = """{"id":1,"name":"Search","status":"queued"}""";

    /// <summary>Assumed shape (replaced by the Sonarr and Radarr recordings, #1013 and #1014): a manager that fails on its own.</summary>
    public const string ManagerError = """{"message":"Internal Server Error"}""";
}
