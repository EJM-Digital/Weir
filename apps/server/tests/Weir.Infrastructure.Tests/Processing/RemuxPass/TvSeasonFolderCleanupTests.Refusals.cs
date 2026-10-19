using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>Every reason a TV season is left in place, and the paths that let it go, on real folders and a real database.</summary>
public sealed partial class TvSeasonFolderCleanupTests
{
    private const string EpisodeRelative = "Serie/S01/e.mkv";

    private async Task<(StoreFixture Store, TvSeasonFolderCleanup Cleanup)> ReadyAsync()
    {
        var (store, cleanup, http, connections, _) = await BuildAsync();
        await store.WithUnitOfWork(uow => connections.CreateAsync(uow, "sonarr", "http://sonarr.local", "key"));
        RouteSonarrQueue(http, "[]");
        return (store, cleanup);
    }

    /// <summary>An episode in the watched folder, with a finished output a hundredth the size or more.</summary>
    private string Episode(string relative = EpisodeRelative)
    {
        var source = _folders.Source(relative, 500);
        Directory.CreateDirectory(Path.GetDirectoryName(_folders.Out(relative))!);
        File.WriteAllBytes(_folders.Out(relative), new byte[100]);
        return source;
    }

    private static List<string> Summary(WireObject output) => ((WireArray)output["tv_episode_check_summary"]!).Items.Select(WireConvert.Str).ToList();

    private static async Task AddActivityAsync(StoreFixture store, string? detail) =>
        await store.Execute(
            "INSERT INTO activity_events (event_type, module, title, detail) VALUES ('processing.file_remux_pass_completed', 'processing', 't', " +
            (detail is null ? "NULL" : "'" + detail.Replace("'", "''", StringComparison.Ordinal) + "'") + ")");

    private static string TvSuccess(Action<Dictionary<string, object?>>? change = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["dry_run"] = false,
            ["media_scope"] = "tv",
            ["relative_media_path"] = EpisodeRelative,
            ["outcome"] = "live_output_written",
        };
        change?.Invoke(fields);
        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public async Task The_cleanup_cannot_be_built_without_any_of_its_parts()
    {
        var (store, cleanup, _, connections, _) = await BuildAsync();
        using var _1 = store;

        Assert.Throws<ArgumentNullException>(() => new TvSeasonFolderCleanup(null!, connections, store.Clock, NullLogger<TvSeasonFolderCleanup>.Instance));
        Assert.Throws<ArgumentNullException>(() => new TvSeasonFolderCleanup(store.Database, null!, store.Clock, NullLogger<TvSeasonFolderCleanup>.Instance));
        Assert.Throws<ArgumentNullException>(() => new TvSeasonFolderCleanup(store.Database, connections, null!, NullLogger<TvSeasonFolderCleanup>.Instance));
        Assert.Throws<ArgumentNullException>(() => new TvSeasonFolderCleanup(store.Database, connections, store.Clock, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => cleanup.RunAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void A_season_folder_that_is_not_there_has_no_episodes()
    {
        Assert.Empty(TvSeasonFolderCleanup.GetTvEpisodeSetMediaFiles(Path.Join(_folders.Watched, "nowhere")));
    }

    [Fact]
    public void A_season_folder_that_cannot_be_listed_has_no_episodes()
    {
        var season = Path.GetDirectoryName(Episode())!;
        using var locked = UnreadablePath.Create(season);
        if (locked is null)
        {
            return;
        }

        Assert.Empty(TvSeasonFolderCleanup.GetTvEpisodeSetMediaFiles(season));
    }

    [Fact]
    public async Task A_file_outside_the_watched_folder_removes_nothing()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var outside = Path.Join(_folders.Root, "elsewhere", "e.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, [1]);

        var output = await RunAsync(cleanup, outside, currentJobId: 1);

        Assert.Equal("The video file is not under the saved TV watched folder, so nothing was removed.", Str(output, "tv_season_folder_skip_reason"));
        Assert.Equal(["Stopped: the processed file is not under the TV watched folder."], Summary(output));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task A_source_that_is_the_watched_folder_itself_removes_nothing()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;

        var output = await RunAsync(cleanup, _folders.Watched, currentJobId: 1);

        Assert.Equal("The season folder would sit outside the TV watched folder, so nothing was done.", Str(output, "tv_season_folder_skip_reason"));
        Assert.Equal(["Stopped: season folder is outside the TV watched folder."], Summary(output));
        Assert.True(Directory.Exists(_folders.Watched));
    }

    [Fact]
    public async Task A_season_with_no_video_files_removes_nothing()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var notes = _folders.Source("Serie/S01/notes.txt", 10);

        var output = await RunAsync(cleanup, notes, currentJobId: 1);

        Assert.Equal("This season folder has no direct video files treated as episodes, so nothing was removed.", Str(output, "tv_season_folder_skip_reason"));
        Assert.Equal(["Stopped: no episode media files found as direct children of the season folder."], Summary(output));
        Assert.True(File.Exists(notes));
    }

    [Fact]
    public async Task A_workflow_with_no_output_folder_removes_nothing()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();

        var output = await RunAsync(cleanup, source, currentJobId: 1, runtime: _folders.Runtime() with { OutputFolder = " " });

        Assert.Equal("No TV output folder is configured, so season cleanup was skipped.", Str(output, "tv_season_folder_skip_reason"));
        Assert.Equal(["Stopped: TV output folder is not configured."], Summary(output));
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData("ok", false)]
    [InlineData("ok", null)]
    [InlineData("dry_run", true)]
    [InlineData("dry_run", null)]
    [InlineData("outcome", "live_failed")]
    [InlineData("outcome", null)]
    public async Task A_pass_that_did_not_write_or_skip_for_real_is_not_proof_the_episode_is_finished(string key, object? value)
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();
        var context = LiveOkContext(EpisodeRelative);
        switch (value)
        {
            case null:
                context.Remove(key);
                break;
            case bool flag:
                context.Set(key, flag);
                break;
            default:
                context.Set(key, (string)value);
                break;
        }

        var output = await RunAsync(cleanup, source, minFileAgeSeconds: 86_400, currentJobId: 1, remuxContext: context, finalOutputFile: _folders.Out(EpisodeRelative));

        Assert.Contains("was never finished in TV mode", Str(output, "tv_season_folder_skip_reason"), StringComparison.Ordinal);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_pass_that_wrote_an_output_counts_the_same_as_one_that_found_nothing_to_change()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();
        var context = LiveOkContext(EpisodeRelative).Set("outcome", "live_output_written");

        var output = await RunAsync(cleanup, source, minFileAgeSeconds: 86_400, currentJobId: 1, remuxContext: context, finalOutputFile: _folders.Out(EpisodeRelative));

        Assert.True(Bool(output, "tv_season_folder_deleted"));
        Assert.Equal("passed", Str((WireObject)output["tv_output_completeness_check"]!, "e.mkv"));
    }

    [Fact]
    public async Task An_output_that_is_not_there_stops_the_cleanup_and_says_why()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();
        File.Delete(_folders.Out(EpisodeRelative));

        var output = await RunAsync(cleanup, source, currentJobId: 1, remuxContext: LiveOkContext(EpisodeRelative));

        Assert.Equal("The output file is missing at the expected path.", Str(output, "tv_season_folder_skip_reason"));
        Assert.Equal("failed", Str((WireObject)output["tv_output_completeness_check"]!, "e.mkv"));
        Assert.EndsWith("Output completeness check failed for this TV pass — The output file is missing at the expected path..", Summary(output).Single(), StringComparison.Ordinal);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task The_output_the_pass_reported_is_the_one_checked_when_no_final_file_is_given()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();
        File.Delete(_folders.Out(EpisodeRelative));
        var renamed = _folders.Out("Serie/S01/renamed.mkv");
        File.WriteAllBytes(renamed, new byte[100]);
        var context = LiveOkContext(EpisodeRelative).Set("output_file", renamed);

        var output = await RunAsync(cleanup, source, currentJobId: 1, remuxContext: context);

        Assert.True(Bool(output, "tv_season_folder_deleted"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(source)));
    }

    [Fact]
    public async Task A_blank_reported_output_falls_back_to_the_same_place_in_the_output_folder()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();
        var context = LiveOkContext(EpisodeRelative).Set("output_file", "  ");

        var output = await RunAsync(cleanup, source, currentJobId: 1, remuxContext: context);

        Assert.True(Bool(output, "tv_season_folder_deleted"));
    }

    [Fact]
    public async Task An_episode_finished_earlier_whose_output_has_gone_keeps_the_season()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();
        File.Delete(_folders.Out(EpisodeRelative));
        await AddActivityAsync(store, TvSuccess());

        var output = await RunAsync(cleanup, source, currentJobId: 1, remuxContext: LiveOkContext("Serie/S01/other.mkv"));

        Assert.Equal("The output file is missing at the expected path.", Str(output, "tv_season_folder_skip_reason"));
        Assert.Contains("Output completeness check failed for a previously finished TV pass", Summary(output).Single(), StringComparison.Ordinal);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Only_a_live_tv_success_for_this_file_in_the_history_counts_as_finished()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();
        var unrelated = LiveOkContext("Serie/S01/other.mkv");

        await AddActivityAsync(store, null);
        await AddActivityAsync(store, "   ");
        await AddActivityAsync(store, "not json {");
        await AddActivityAsync(store, "[1]");
        await AddActivityAsync(store, TvSuccess(f => f["relative_media_path"] = "Serie/S01/other.mkv"));
        await AddActivityAsync(store, TvSuccess(f => f.Remove("relative_media_path")));
        await AddActivityAsync(store, TvSuccess(f => f.Remove("media_scope")));
        await AddActivityAsync(store, TvSuccess(f => f["media_scope"] = "movie"));
        await AddActivityAsync(store, TvSuccess(f => f["dry_run"] = true));
        await AddActivityAsync(store, TvSuccess(f => f["ok"] = false));
        await AddActivityAsync(store, TvSuccess(f => f.Remove("ok")));
        await AddActivityAsync(store, TvSuccess(f => f["outcome"] = "live_failed"));
        await AddActivityAsync(store, TvSuccess(f => f.Remove("outcome")));

        var before = await RunAsync(cleanup, source, minFileAgeSeconds: 86_400, currentJobId: 1, remuxContext: unrelated);

        Assert.Contains("was never finished in TV mode", Str(before, "tv_season_folder_skip_reason"), StringComparison.Ordinal);
        Assert.True(File.Exists(source));

        await AddActivityAsync(store, TvSuccess(f =>
        {
            f["relative_media_path"] = " " + EpisodeRelative + " ";
            f.Remove("dry_run");
        }));

        var after = await RunAsync(cleanup, source, minFileAgeSeconds: 86_400, currentJobId: 1, remuxContext: unrelated);

        Assert.True(Bool(after, "tv_season_folder_deleted"));
    }

    [Fact]
    public async Task An_episode_never_finished_is_left_to_the_clock_only_when_the_workflow_waits()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();

        var output = await RunAsync(cleanup, source, minFileAgeSeconds: 0, currentJobId: 1);

        Assert.True(Bool(output, "tv_season_folder_deleted"));
        Assert.Equal("skipped", Str((WireObject)output["tv_output_completeness_check"]!, "e.mkv"));
        Assert.Contains("Never-processed check passed", Summary(output).First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_season_behind_a_link_is_left_alone()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var realShow = Path.Join(_folders.Root, "elsewhere", "Serie");
        Directory.CreateDirectory(Path.Join(realShow, "S01"));
        File.WriteAllBytes(Path.Join(realShow, "S01", "e.mkv"), new byte[500]);
        FolderLinks.Create(Path.Join(_folders.Watched, "Serie"), realShow);
        Directory.CreateDirectory(Path.GetDirectoryName(_folders.Out(EpisodeRelative))!);
        File.WriteAllBytes(_folders.Out(EpisodeRelative), new byte[100]);

        var output = await RunAsync(
            cleanup, Path.Join(_folders.Watched, "Serie", "S01", "e.mkv"), currentJobId: 1, remuxContext: LiveOkContext(EpisodeRelative), finalOutputFile: _folders.Out(EpisodeRelative));

        Assert.Equal(ReleaseFolderRemoval.LinkedFolderReason, Str(output, "tv_season_folder_skip_reason"));
        Assert.False(output.ContainsKey("tv_season_folder_deleted") && Bool(output, "tv_season_folder_deleted"));
        Assert.True(File.Exists(Path.Join(realShow, "S01", "e.mkv")));
    }

    [WindowsFact("Only Windows refuses to delete a folder holding a file another process has open.")]
    public async Task A_season_with_a_file_held_open_stays_and_the_reason_says_it_is_in_use()
    {
        var (store, cleanup) = await ReadyAsync();
        using var _1 = store;
        var source = Episode();
        using var holder = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);

        var output = await RunAsync(cleanup, source, currentJobId: 1, remuxContext: LiveOkContext(EpisodeRelative), finalOutputFile: _folders.Out(EpisodeRelative));

        Assert.Contains("in use or locked", Str(output, "tv_season_folder_skip_reason"), StringComparison.Ordinal);
        Assert.StartsWith("Deletion failed: ", Summary(output).Last(), StringComparison.Ordinal);
        Assert.True(File.Exists(source));
    }
}
