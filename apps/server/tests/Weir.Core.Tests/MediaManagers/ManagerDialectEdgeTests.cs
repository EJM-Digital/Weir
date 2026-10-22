using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// How Weir reads the shapes a media manager may answer with when the answer is not the usual one: spellings of a media type,
/// containers that are missing or odd, and the sentence a person sees when a manager cannot be reached.
/// </summary>
public sealed class ManagerDialectEdgeTests
{
    private static readonly ManagerConnection Connection = new("sonarr", "Sonarr", "http://localhost:8989", "key");

    private static WireValue Json(string text) => WireJsonParser.Parse(text);

    [Theory]
    [InlineData("movie", "movie")]
    [InlineData("Movies", "movie")]
    [InlineData("FILM", "movie")]
    [InlineData("films", "movie")]
    [InlineData("tv", "tv")]
    [InlineData("series", "tv")]
    [InlineData("Show", "tv")]
    [InlineData("shows", "tv")]
    [InlineData("episode", "tv")]
    [InlineData("Episodes", "tv")]
    [InlineData("music", null)]
    [InlineData("", null)]
    public void A_media_type_is_read_in_any_of_its_spellings(string spelling, string? scope)
    {
        Assert.Equal(scope, ManagerDialectRules.ScopeFromMediaType(new WireString(spelling)));
    }

    [Fact]
    public void A_missing_media_type_names_no_scope()
    {
        Assert.Null(ManagerDialectRules.ScopeFromMediaType(null));
        Assert.Null(ManagerDialectRules.ScopeFromMediaType(WireNull.Instance));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(0.0, "")]
    [InlineData(30.7, " It asked Weir to wait about 30s.")]
    public void A_manager_that_rate_limits_says_how_long_it_asked_Weir_to_wait(double? seconds, string wait)
    {
        var message = ManagerDialectRules.Unreachable(Connection, new MediaManagerRateLimitedException("429", seconds), "its queue");

        Assert.Equal($"Sonarr is rate limiting Weir, so it could not say its queue.{wait} Weir backed off rather than retrying straight away.", message);
    }

    [Fact]
    public void A_manager_that_answers_with_a_forbidden_status_is_said_to_have_refused_the_key()
    {
        var message = ManagerDialectRules.Unreachable(Connection, new MediaManagerHttpException("HTTP 403 from the manager"), "its queue");

        Assert.Contains("refused Weir's API key", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manager_that_answers_with_something_unexpected_is_said_to_have_given_the_wrong_answer()
    {
        var message = ManagerDialectRules.Unreachable(Connection, new MediaManagerHttpException("HTTP 500 from the manager"), "its queue");

        Assert.Equal(
            "Sonarr did not give Weir the answer it expected when asked its queue. " +
            "Check the address points at Sonarr itself, not a page inside it, and that Sonarr is running normally.",
            message);
    }

    [Fact]
    public void A_manager_that_sent_Weir_elsewhere_says_so_in_its_own_words()
    {
        var message = ManagerDialectRules.Unreachable(Connection, new MediaManagerRedirectedException(), "its queue");

        Assert.Equal("Sonarr answered with a redirect to another address. Use the address it redirects to.", message);
    }

    [Fact]
    public void A_manager_that_cannot_be_reached_at_all_gets_the_usual_sentence()
    {
        var usual = ManagerDialectRules.Unreachable(Connection, new HttpRequestException("refused"), "its queue");
        var unreadable = ManagerDialectRules.Unreachable(
            Connection, new MediaManagerHttpException("bad address", new WireValueException("bad")), "its queue");

        Assert.Contains("Sonarr", usual, StringComparison.Ordinal);
        Assert.Equal(usual, unreadable);
    }

    [Theory]
    [InlineData("""{"records": [{"id": 1}, {"id": 2}]}""", 2)]
    [InlineData("""[{"id": 1}]""", 1)]
    [InlineData("""{"other": []}""", 0)]
    [InlineData("null", 0)]
    [InlineData("0", 0)]
    [InlineData("\"\"", 0)]
    public void A_queue_is_read_from_a_list_or_from_the_records_of_an_object(string payload, int rows)
    {
        Assert.Equal(rows, ManagerDialectRules.ArrQueueRows(Json(payload), "tv").Count);
        Assert.All(ManagerDialectRules.ArrQueueRows(Json(payload), "tv"), row => Assert.Equal("tv", row.Scope));
    }

    [Fact]
    public void A_missing_queue_answer_is_no_rows()
    {
        Assert.Empty(ManagerDialectRules.ArrQueueRows(null, "tv"));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"queue\"")]
    public void A_queue_answer_that_is_neither_a_list_nor_an_object_is_refused(string payload)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => ManagerDialectRules.ArrQueueRows(Json(payload), "tv"));

        Assert.Equal("The media manager's queue answer was neither a list nor an object with records.", thrown.Message);
    }

    [Fact]
    public void A_series_or_movie_with_no_id_is_left_out_of_the_library_files()
    {
        var movies = ManagerDialectRules.ArrMovieLibraryFiles(Json("""
            [{"title": "No id", "movieFile": {"path": "/m/a.mkv"}},
             {"id": 3, "title": "No file"},
             {"id": 4, "title": "No path", "movieFile": {"id": 9}},
             {"id": 5, "movieFile": {"id": 10, "path": "/m/b.mkv"}, "qualityProfileId": 2}]
            """));

        var only = Assert.Single(movies);
        Assert.Equal(("5", "5", "/m/b.mkv", 10L, 2L), (only.TitleId, only.TitleName, only.FilePath, only.FileId, only.QualityProfileId));
    }

    [Fact]
    public void A_queue_row_with_no_id_is_left_out_of_the_series_list()
    {
        Assert.Empty(ManagerDialectRules.ArrLibraryFilePaths(Json("""[{"path": 5}, {"movieFile": 1}]"""), "movieFile"));
        Assert.Equal(["/m/a.mkv"], ManagerDialectRules.ArrLibraryFilePaths(Json("""[{"movieFile": {"path": "/m/a.mkv"}}]"""), "movieFile"));
        Assert.Equal(["/tv/a.mkv"], ManagerDialectRules.ArrLibraryFilePaths(Json("""[{"path": "/tv/a.mkv"}]"""), null));
    }

    [Fact]
    public void A_manifest_names_its_libraries_under_the_first_key_that_has_any()
    {
        Assert.Equal(["a"], ManagerDialectRules.ManifestLibraries(Json("""{"libraries": [], "roots": [{"id": "a"}]}""")).Select(l => ((WireString)l["id"]).Value));
        Assert.Empty(ManagerDialectRules.ManifestLibraries(Json("""{"libraries": []}""")));
        Assert.Single(ManagerDialectRules.ManifestLibraries(Json("""[{"id": "a"}]""")));
        Assert.Empty(ManagerDialectRules.ManifestLibraries(null));
    }

    [Fact]
    public void A_manifest_without_download_clients_or_capabilities_has_none()
    {
        Assert.Empty(ManagerDialectRules.ManifestDownloadClients(null));
        Assert.Empty(ManagerDialectRules.ManifestDownloadClients(Json("""[1]""")));
        Assert.Empty(ManagerDialectRules.ManifestCapabilities(null));
        Assert.Empty(ManagerDialectRules.ManifestCapabilities(Json("""{"capabilities": "queue"}""")));
        Assert.Empty(ManagerDialectRules.ManifestCapabilities(Json("""{"capabilities": ["  ", 4]}""")));
        Assert.Equal(["queue", "scan"], ManagerDialectRules.ManifestCapabilities(Json("""{"capabilities": [" Queue ", "SCAN", "queue"]}""")));
    }

    [Fact]
    public void A_library_key_is_its_number_or_its_text_or_nothing()
    {
        Assert.Equal("7", ManagerDialectRules.ManifestLibraryKey((WireObject)Json("""{"id": 7}""")));
        Assert.Equal("abc", ManagerDialectRules.ManifestLibraryKey((WireObject)Json("""{"id": "abc"}""")));
        Assert.Null(ManagerDialectRules.ManifestLibraryKey((WireObject)Json("""{"other": 1}""")));
        Assert.Equal("9", ManagerDialectRules.ManifestLibraryKey((WireObject)Json("""{"libraryId": 9}"""), "libraryId"));
    }

    [Fact]
    public void A_library_with_no_id_or_name_is_described_with_an_empty_key()
    {
        var described = ManagerDialectRules.ManifestLibraryDescriptor((WireObject)Json("""{"mediaType": "series"}"""));

        Assert.Equal(string.Empty, described.Key);
        Assert.Equal(string.Empty, described.Name);
        Assert.Equal(MediaManagerKinds.Tv, described.MediaScope);
    }

    [Fact]
    public void A_queue_is_read_from_every_container_a_manager_may_use()
    {
        Assert.Single(ManagerDialectRules.ExternalQueueEntries(Json("""[{"id": 1}]""")));
        Assert.Equal(3, ManagerDialectRules.ExternalQueueEntries(Json("""{"jobs": [{"id": 1}], "items": [{"id": 2}, {"id": 3}], "x": [{"id": 4}]}""")).Count);
        Assert.Empty(ManagerDialectRules.ExternalQueueEntries(Json("""12""")));
        Assert.Empty(ManagerDialectRules.ExternalQueueEntries(null));
    }
}
