using System.Net;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// Hand-off lifecycle for media managers: running work, cancelling, and retention. Auth and status are in
/// <see cref="HandoffStatusTests"/>; folder hand-offs are in <see cref="HandoffFolderTests"/>.
/// </summary>
[ContractArea("media_managers")]
public sealed class HandoffLifecycleTests(HandoffServerFixture fixture) : IClassFixture<HandoffServerFixture>
{
    private WeirServer Server => fixture.Server;

    // --- running work: a real worker with a fake ffmpeg ------------------------------------------------

    [Fact]
    public async Task A_running_pass_is_working()
    {
        using var temp = new TemporaryFolder();
        var release = Path.Combine(temp.Path, "release-remux");
        using var tools = FakeFfmpeg.Install();
        tools.SetFileRule("*.mkv", new FileRule { Probe = FakeMedia.Probe(), RemuxReleaseFile = release });
        await using var working = await WorkingServer.StartAsync(tools, temp.Path);
        await Handoffs.HandOffAsync(working.Server, "h1", working.Source);

        await working.WaitUntilLeasedAsync("h1");

        Assert.Equal("working", (string)(await Handoffs.StatusAsync(working.Server, "h1"))["state"]!);
        await File.WriteAllTextAsync(release, string.Empty);
    }

    [Fact]
    public async Task The_report_records_the_output_path()
    {
        // A worker finishes the file and reports.
        using var temp = new TemporaryFolder();
        using var tools = FakeFfmpeg.Install();
        tools.SetFileRule("*.mkv", new FileRule { Probe = FakeMedia.Probe() });
        await using var working = await WorkingServer.StartAsync(tools, temp.Path);
        await Handoffs.HandOffAsync(working.Server, "h1", working.Source);

        var body = await new JobWait().UntilAsync(
            () => RemuxJobs.StatusesForHandoffAsync(working.Admin, "h1"),
            async () =>
            {
                var answer = await Handoffs.StatusAsync(working.Server, "h1");
                return (string)answer["state"]! is "queued" or "working" ? null : answer;
            },
            "the hand-off to finish");

        await working.Deluno.WaitForRequestAsync("POST", RemuxJobs.CallbackPath);
        Assert.True((string)body["state"]! == "completed", body.ToJsonString());
        var outputPath = (string)body["outputPath"]!;
        Assert.NotEmpty(outputPath);
        Assert.Equal("film.mkv", Path.GetFileName(outputPath));
        Assert.True(File.Exists(outputPath), outputPath);
        Assert.True(IsInside(working.Folders.Output, outputPath), $"{outputPath} is not inside {working.Folders.Output}");
    }

    // --- cancel ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_hand_off_can_be_cancelled()
    {
        var movies = await fixture.MoviesAsync();
        var handoffId = Handoffs.NewId();
        await Handoffs.HandOffAsync(Server, handoffId, Path.Combine(movies.Watched, handoffId, "film.mkv"));

        var cancelled = await Handoffs.CancelAsync(Server, handoffId);

        Assert.True(cancelled.Status == HttpStatusCode.NoContent, cancelled.ToString());
        using var admin = await Server.CreateAdminClientAsync();
        Assert.Equal("cancelled", await RemuxJobs.StatusForHandoffAsync(admin, handoffId));
        var events = await admin.GetAsync(
            $"{WeirClient.Api}/activity/recent", ("event_type", "processing.handoff_cancelled"), ("limit", 100));
        Assert.True(events.Status == HttpStatusCode.OK, events.ToString());
        var titles = events.Fields["items"]!.AsArray().Select(item => (string)item!["title"]!).ToList();
        Assert.True(titles.Any(title => title.Contains("film.mkv", StringComparison.Ordinal)), string.Join(" | ", titles));
        Assert.Equal("cancelled", (string)(await Handoffs.StatusAsync(Server, handoffId))["state"]!);
        // A second cancel is refused: it is already finished.
        Assert.Equal(HttpStatusCode.Conflict, (await Handoffs.CancelAsync(Server, handoffId)).Status);
    }

    [Fact]
    public async Task A_cancel_that_carries_an_oversized_body_is_refused_and_the_hand_off_stays_queued()
    {
        var movies = await fixture.MoviesAsync();
        var handoffId = Handoffs.NewId();
        await Handoffs.HandOffAsync(Server, handoffId, Path.Combine(movies.Watched, handoffId, "film.mkv"));
        var nineMegabytes = new byte[9 * 1024 * 1024];
        using var manager = Server.CreateClient();

        var withoutTheSecret = await manager.RequestRawAsync(HttpMethod.Delete, Handoffs.StatusPath(handoffId), nineMegabytes, "application/json");
        var withTheSecret = await manager.RequestRawAsync(HttpMethod.Delete, Handoffs.StatusPath(handoffId), nineMegabytes, "application/json", Handoffs.Secret);

        Assert.True(withoutTheSecret.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, withoutTheSecret.ToString());
        Assert.True(withTheSecret.Status == HttpStatusCode.RequestEntityTooLarge, withTheSecret.ToString());
        Assert.Equal("queued", (string)(await Handoffs.StatusAsync(Server, handoffId))["state"]!);
    }

    [Fact]
    public async Task Work_that_has_started_is_never_cancelled()
    {
        using var temp = new TemporaryFolder();
        var release = Path.Combine(temp.Path, "release-remux");
        using var tools = FakeFfmpeg.Install();
        tools.SetFileRule("*.mkv", new FileRule { Probe = FakeMedia.Probe(), RemuxReleaseFile = release });
        await using var working = await WorkingServer.StartAsync(tools, temp.Path);
        await Handoffs.HandOffAsync(working.Server, "h1", working.Source);
        await working.WaitUntilLeasedAsync("h1");

        var response = await Handoffs.CancelAsync(working.Server, "h1");

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Contains("working", (string)response.Fields["detail"]!);
        Assert.Equal("leased", await working.JobStatusAsync("h1"));
        await File.WriteAllTextAsync(release, string.Empty);
    }

    [Fact]
    public async Task Cancelling_an_unknown_hand_off_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Handoffs.CancelAsync(Server, "nope")).Status);

    [Fact]
    public async Task A_manager_resending_a_cancelled_hand_off_starts_it_again()
    {
        var movies = await fixture.MoviesAsync();
        var handoffId = Handoffs.NewId();
        var source = Path.Combine(movies.Watched, handoffId, "film.mkv");
        await Handoffs.HandOffAsync(Server, handoffId, source);
        Assert.Equal(HttpStatusCode.NoContent, (await Handoffs.CancelAsync(Server, handoffId)).Status);

        await Handoffs.HandOffAsync(Server, handoffId, source);

        Assert.Equal("queued", (string)(await Handoffs.StatusAsync(Server, handoffId))["state"]!);
    }

    // --- retention ---------------------------------------------------------------------------------

    [Fact]
    public async Task Only_old_finished_hand_offs_are_pruned()
    {
        // The retention tick that runs at startup does the pruning.
        var old = SeedSql.UtcText(DateTime.UtcNow - TimeSpan.FromDays(Handoffs.LedgerRetentionDays + 1));
        string done = Handoffs.NewId(), waiting = Handoffs.NewId();
        await using (var database = await Server.StopForDatabaseAsync())
        {
            foreach (var (handoffId, state) in new[] { (done, "completed"), (waiting, "queued") })
            {
                SeedSql.Execute(
                    database.Connection,
                    "INSERT INTO media_manager_handoffs (source_key, handoff_id, relative_path, state, created_at, last_changed_at) "
                        + "VALUES ('deluno', $id, 'x.mkv', $state, $old, $old)",
                    ("$id", handoffId), ("$state", state), ("$old", old));
            }
        }

        await Poll.UntilAsync(
            async () => (await Handoffs.StatusResponseAsync(Server, done)).Status == HttpStatusCode.NotFound,
            "the old finished hand-off to be pruned",
            TimeSpan.FromSeconds(30));

        Assert.Equal("queued", (string)(await Handoffs.StatusAsync(Server, waiting))["state"]!);
    }

    private static bool IsInside(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        for (var parent = Path.GetDirectoryName(Path.GetFullPath(path)); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(parent), root, comparison))
            {
                return true;
            }
        }

        return false;
    }
}
