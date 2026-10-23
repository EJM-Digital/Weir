using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>The small decisions around a media manager: which local file it means, what a job payload must hold, and what is forgotten.</summary>
public sealed class ManagerHelperEdgeTests
{
    private static ManagerLibraryDescriptor Library(string? root) => new("key", "Movies", "movie", root);

    [Fact]
    public void Two_paths_are_equal_when_they_name_the_same_file_whatever_the_separator_and_nothing_equals_nothing()
    {
        Assert.True(LibraryTitleMatcher.PathsEqual(@"\data\film.mkv", "/data/film.mkv"));
        Assert.True(LibraryTitleMatcher.PathsEqual("/data/film/", "/data/film"));
        Assert.False(LibraryTitleMatcher.PathsEqual(null, null));
        Assert.False(LibraryTitleMatcher.PathsEqual("/data/film.mkv", null));
        Assert.False(LibraryTitleMatcher.PathsEqual(null, "/data/film.mkv"));
        Assert.False(LibraryTitleMatcher.PathsEqual("/", "/"));
    }

    [Fact]
    public void A_manager_path_that_is_one_of_ours_is_matched_directly()
    {
        var match = LibraryTitleMatcher.MatchLocalPath("/data/film.mkv", ["/data/other.mkv", "/data/film.mkv"], [], []);

        Assert.Equal("/data/film.mkv", match);
    }

    [Fact]
    public void A_manager_path_is_matched_through_the_root_the_manager_keeps_the_library_in()
    {
        var match = LibraryTitleMatcher.MatchLocalPath(
            "/manager/movies/Film/film.mkv",
            ["/local/movies/Film/film.mkv"],
            [Library(null), Library("/elsewhere"), Library("/manager/movies")],
            ["/local/movies"]);

        Assert.Equal("/local/movies/Film/film.mkv", match);
    }

    [Fact]
    public void A_manager_path_none_of_our_files_matches_is_unmatched()
    {
        var match = LibraryTitleMatcher.MatchLocalPath(
            "/manager/movies/Film/film.mkv",
            ["/local/movies/Other/other.mkv"],
            [Library("/manager/movies")],
            ["/local/movies"]);

        Assert.Null(match);
    }

    [Fact]
    public async Task A_workflow_that_left_is_forgotten_so_its_return_is_not_announced_as_a_change()
    {
        var changes = new DataChangePublisher();
        var answers = new FolderChainAnswers(changes);
        using var stream = changes.Subscribe();
        var chain = new WireObject().Set("answer", "reachable");
        answers.Given(1, chain);
        answers.Given(2, chain);

        answers.KeepOnly(new HashSet<long> { 2 });
        answers.Given(1, new WireObject().Set("answer", "missing"));
        answers.Given(2, new WireObject().Set("answer", "missing"));
        changes.Publish(DataTopics.Jobs);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var heard = new List<string>();
        await foreach (var topic in stream.ReadAllAsync(timeout.Token))
        {
            heard.Add(topic);
            if (topic == DataTopics.Jobs)
            {
                break;
            }
        }

        Assert.Equal([DataTopics.FolderChecks, DataTopics.Jobs], heard);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"arr_scope\": \"movie\", \"output_path\": \"/x\"}")]
    [InlineData("{\"connection_id\": 1, \"output_path\": \"/x\"}")]
    [InlineData("{\"connection_id\": 1, \"arr_scope\": \"movie\", \"output_path\": \"\"}")]
    public void A_job_payload_that_is_not_a_downloaded_scan_request_is_not_read_as_one(string? payload)
    {
        Assert.Null(DownloadedScanRequest.Parse(payload));
    }

    [Fact]
    public async Task A_health_check_with_no_address_asks_for_one_without_calling_anyone()
    {
        var http = new FakeManagerHttp();

        var (ok, detail) = await ManagerHealthProbe.ProbeAsync(http, "Main", "radarr", "  ", "key", TimeSpan.FromSeconds(1), null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("Add the address where this app can be reached, then test again.", detail);
        Assert.Empty(http.Requests);
    }
}
