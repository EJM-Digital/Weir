using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>Which inbound events from a media manager are read, and which are turned away because they do not say what file they mean.</summary>
public sealed class ImportEventEdgeTests
{
    private static WireObject Json(string json) => (WireObject)WireJsonParser.Parse(json);

    private static MediaManagerImportEvent? Normalize(string source, string json) => ImportEvents.DialectForSource(source)!.Normalize(Json(json));

    [Fact]
    public void A_source_key_is_matched_without_regard_to_case_or_spaces()
    {
        Assert.Equal("sonarr", ImportEvents.DialectForSource("  SONARR ")!.Key);
        Assert.Null(ImportEvents.DialectForSource("plex"));
        Assert.Null(ImportEvents.DialectForSource(null));
        Assert.Equal(["deluno", "native", "radarr", "sonarr"], ImportEvents.KnownSourceKeys());
    }

    [Theory]
    [InlineData("""{"eventType": "Grab", "episodes": [{"id": 1}], "episodeFile": {"path": "/tv/a.mkv"}}""")]
    [InlineData("""{"eventType": "Download", "episodeFile": {"path": "/tv/a.mkv"}}""")]
    [InlineData("""{"eventType": "Download", "episodes": [], "episodeFile": {"path": "/tv/a.mkv"}}""")]
    [InlineData("""{"eventType": "Download", "episodes": [7], "episodeFile": {"path": "/tv/a.mkv"}}""")]
    [InlineData("""{"eventType": "Download", "episodes": [{"id": 1}]}""")]
    [InlineData("""{"eventType": "Download", "episodes": [{"id": 1}], "episodeFile": {"quality": "HD"}}""")]
    public void A_sonarr_event_that_does_not_name_an_imported_episode_file_is_turned_away(string body)
    {
        Assert.Null(Normalize("sonarr", body));
    }

    [Theory]
    [InlineData("""{"eventType": "Grab", "movie": {"id": 1}, "movieFile": {"path": "/m/a.mkv"}}""")]
    [InlineData("""{"eventType": "Download", "movieFile": {"path": "/m/a.mkv"}}""")]
    [InlineData("""{"eventType": "Download", "movie": {"id": 1}}""")]
    [InlineData("""{"eventType": "Download", "movie": {"id": 1}, "movieFile": {"quality": "HD"}}""")]
    public void A_radarr_event_that_does_not_name_an_imported_movie_file_is_turned_away(string body)
    {
        Assert.Null(Normalize("radarr", body));
    }

    [Theory]
    [InlineData("-1", null)]
    [InlineData("99999999999", null)]
    [InlineData("0", 0)]
    [InlineData("4", 4)]
    public void An_episode_number_that_cannot_be_an_artwork_number_is_left_out_of_the_hints(string season, int? expected)
    {
        var imported = Normalize("sonarr", $$$"""
            {"eventType": "Download", "series": {"id": 3, "title": "Paper Lanterns", "tvdbId": 121361},
             "episodes": [{"id": 41, "seasonNumber": {{{season}}}, "episodeNumber": 2}], "episodeFile": {"path": "/tv/x.mkv"}}
            """);

        Assert.Equal(expected, imported!.Artwork!.Season);
        Assert.Equal(2, imported.Artwork.Episode);
    }

    [Theory]
    [InlineData("movie", "movie")]
    [InlineData("Movies", "movie")]
    [InlineData("FILM", "movie")]
    [InlineData("tv", "tv")]
    [InlineData("Series", "tv")]
    [InlineData("show", "tv")]
    [InlineData("episode", "tv")]
    [InlineData("music", null)]
    public void An_inbound_media_type_is_read_in_the_spellings_a_manager_uses(string spelling, string? scope)
    {
        Assert.Equal(scope, ImportEvents.ScopeFromMediaType(new WireString(spelling)));
        Assert.Null(ImportEvents.ScopeFromMediaType(null));
    }

    [Theory]
    [InlineData("""{"eventType": "other", "sourcePath": "/in/a.mkv", "mediaType": "movie"}""")]
    [InlineData("""{"eventType": "deluno.processor-handoff", "mediaType": "movie"}""")]
    [InlineData("""{"eventType": "deluno.processor-handoff", "sourcePath": "/in/a.mkv"}""")]
    [InlineData("""{"eventType": "deluno.processor-handoff", "sourcePath": "/in/a.mkv", "mediaType": "music"}""")]
    public void A_deluno_event_that_is_not_a_hand_off_of_a_file_is_turned_away(string body)
    {
        Assert.Null(Normalize("deluno", body));
    }

    [Theory]
    [InlineData("""null""", null)]
    [InlineData("""["/in/a.mkv", 5]""", "/in/a.mkv|")]
    [InlineData("""["/in/a.mkv"]""", "/in/a.mkv")]
    [InlineData("""[]""", "")]
    [InlineData("\"/in/a.mkv\"", "")]
    public void The_files_a_hand_off_names_are_kept_and_any_that_are_not_text_are_kept_as_empty(string sourceFiles, string? expected)
    {
        var imported = Normalize("deluno", $$$"""
            {"eventType": "deluno.processor-handoff", "sourcePath": "/in", "mediaType": "movie", "sourceFiles": {{{sourceFiles}}}}
            """);

        Assert.Equal(expected, imported!.SourceFiles is null ? null : string.Join('|', imported.SourceFiles));
    }

    [Fact]
    public void A_native_event_with_no_kind_is_an_import()
    {
        var imported = Normalize("native", """{"filePath": "/m/a.mkv", "mediaScope": "movie", "title": "A"}""")!;

        Assert.Equal((MediaManagerImportEvent.Imported, "/m/a.mkv", "movie", "A"), (imported.EventKind, imported.FilePath, imported.MediaScope, imported.Title));
    }

    [Fact]
    public void A_native_event_may_use_snake_case_names_throughout()
    {
        var imported = Normalize("native", """
            {"event": "HANDOFF", "file_path": "/tv/a.mkv", "media_scope": "tv", "show_title": "Show", "season_number": 2, "episode_number": 5,
             "episode_title": "Ep", "entity_id": 9, "handoff_id": "h1", "callback_path": "/cb", "release_name": "Rel", "library_id": "L",
             "source_path": "/src/a.mkv", "download_id": "d1"}
            """)!;

        Assert.Equal(MediaManagerImportEvent.Handoff, imported.EventKind);
        Assert.Equal(("Show", 2, 5, "Ep"), (imported.ShowTitle, (int)imported.SeasonNumber!.Value, (int)imported.EpisodeNumber!.Value, imported.EpisodeTitle));
        Assert.Equal(("h1", "/cb", "Rel", "L", "/src/a.mkv", "d1"), (imported.HandoffId, imported.CallbackPath, imported.ReleaseName, imported.LibraryId, imported.SourcePath, imported.DownloadId));
        Assert.Equal(9, (int)imported.SourceEntityId!.Value);
    }

    [Theory]
    [InlineData("""{"event": "deleted", "filePath": "/m/a.mkv", "mediaScope": "movie"}""")]
    [InlineData("""{"mediaScope": "movie"}""")]
    [InlineData("""{"filePath": "/m/a.mkv"}""")]
    [InlineData("""{"filePath": "/m/a.mkv", "mediaScope": "music"}""")]
    public void A_native_event_that_is_not_an_import_or_hand_off_of_a_file_is_turned_away(string body)
    {
        Assert.Null(Normalize("native", body));
    }
}
