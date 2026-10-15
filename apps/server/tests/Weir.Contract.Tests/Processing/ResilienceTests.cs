using System.Net;
using System.Text.Json.Nodes;
using TimeZoneConverter;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// Work that is interrupted, paused, or outside its schedule. Pause and the schedule window are checked when a worker claims a
/// job; the claim itself leaves no trace, but <c>GET /processing/files-at-once</c> (#633) reads the same admission rules and says
/// which one a due, queued file is waiting on, so the "nothing starts" claims here are read from that state (<c>waiting_for</c>)
/// rather than watched for an absence. Server-side coverage: <c>FilesAtOnceTests</c> (the rule) and
/// <c>ProcessingFilesAtOnceApiTests</c> (the endpoint), both in the .NET test suite.
/// </summary>
[ContractArea("processing")]
public sealed class ResilienceTests
{
    private const int SlotsPerDay = 96;
    private const string FilesAtOncePath = $"{WeirClient.Api}/processing/files-at-once";

    [Fact]
    public async Task Server_killed_mid_job_recovers_the_job_on_restart_and_finishes_it()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync();
        // The first remux hangs long enough to be killed in the middle of it.
        scenario.FakeTools.SetFileRule("film.mkv", new FileRule { RemuxDelaySeconds = 120 });
        var source = WriteSource(scenario, "Crash.Test.2020");

        await scenario.PostHandoffAsync("handoff-crash-1", source);
        await scenario.WaitWhileWorkedOnAsync(
            () => Task.FromResult(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count > 0), "the remux to start");
        Assert.Equal("working", (string)(await scenario.HandoffStatusAsync("handoff-crash-1"))["state"]!);

        await scenario.Server.StopAsync();
        scenario.FakeTools.SetFileRule("film.mkv");
        await scenario.RestartServerAsync();

        await scenario.WaitForHandoffStateAsync("handoff-crash-1", "completed");
        Assert.Equal(2, scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux").Count);
        Assert.True(File.Exists(Path.Combine(scenario.Folders.Output, "Crash.Test.2020", "film.mkv")));
        var reports = Scenario.Callbacks(fake, "handoff-crash-1");
        Assert.Equal(["completed"], reports.Select(report => (string)report["status"]!));
        var remuxJobs = await scenario.JobsAsync(Scenario.RemuxKind);
        var job = Assert.Single(remuxJobs);
        Assert.Equal("completed", (string)job["status"]!);
        Assert.Equal(2, (int)job["attempt_count"]!);
        // Nothing half-written is published: no partial file in the output folder.
        Assert.Empty(Directory.GetFiles(scenario.Folders.Output, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Pause_stops_new_work_until_resumed()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, _) = await scenario.DelunoSetupAsync();
        var paused = await scenario.SetPauseAsync(paused: true);
        Assert.StartsWith("Processing is paused", (string)paused["reason"]!, StringComparison.Ordinal);
        var source = WriteSource(scenario, "Paused.Film.2021");

        await scenario.PostHandoffAsync("handoff-paused-1", source);
        var waiting = await WaitForWaitingReasonAsync(scenario, "paused");
        Assert.Equal("1 file is waiting because processing is paused.", (string)waiting["message"]!);
        Assert.Equal("queued", (string)(await scenario.HandoffStatusAsync("handoff-paused-1"))["state"]!);
        Assert.Equal(["pending"], (await scenario.JobsAsync(Scenario.RemuxKind)).Select(job => (string)job["status"]!));
        Assert.Empty(Scenario.Callbacks(fake, "handoff-paused-1"));

        await scenario.SetPauseAsync(paused: false);
        await scenario.WaitForHandoffStateAsync("handoff-paused-1", "completed");
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
    }

    [Fact]
    public async Task Schedule_window_blocks_work_outside_its_hours()
    {
        await using var scenario = await Scenario.StartAsync();
        var settings = await scenario.Admin.GetAsync($"{WeirClient.Api}/suite/settings");
        Assert.Equal(HttpStatusCode.OK, settings.Status);
        var timezoneName = (string?)settings.Fields["app_timezone"] is { Length: > 0 } configured ? configured : "UTC";
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZConvert.GetTimeZoneInfo(timezoneName)).DateTime;
        var closedNow = GridClosedAround(localNow);
        var (fake, library) = await scenario.DelunoSetupAsync(library: [("schedule_enabled", true), ("schedule_grid", closedNow)]);
        Assert.Equal(closedNow, (string)library["schedule_grid"]!);
        var source = WriteSource(scenario, "Night.Only.2022");

        await scenario.PostHandoffAsync("handoff-window-1", source);
        var waiting = await WaitForWaitingReasonAsync(scenario, "library_closed");
        Assert.Equal($"1 file is waiting for {(string)library["name"]!}'s schedule to open.", (string)waiting["message"]!);
        Assert.Equal("queued", (string)(await scenario.HandoffStatusAsync("handoff-window-1"))["state"]!);
        Assert.Equal(["pending"], (await scenario.JobsAsync(Scenario.RemuxKind)).Select(job => (string)job["status"]!));

        // Opening the window lets the waiting job start.
        await scenario.UpdateLibraryAsync(library, ("schedule_grid", string.Empty));
        await scenario.WaitForHandoffStateAsync("handoff-window-1", "completed");
        Assert.Equal(["completed"], Scenario.Callbacks(fake, "handoff-window-1").Select(report => (string)report["status"]!));
    }

    private static async Task<JsonObject> WaitForWaitingReasonAsync(Scenario scenario, string reason) =>
        await Poll.UntilAsync(
            async () =>
            {
                var response = await scenario.Admin.GetAsync(FilesAtOncePath);
                Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
                return (string)response.Fields["waiting_for"]! == reason ? response.Fields : null;
            },
            $"the read-out to say '{reason}'",
            TimeSpan.FromSeconds(15));

    private static string WriteSource(Scenario scenario, string release) =>
        scenario.WriteRelease(release, "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));

    // A week of quarter-hour slots, Monday first: open all week except three hours either side of the given local time.
    private static string GridClosedAround(DateTime localNow, int hours = 3)
    {
        var grid = Enumerable.Repeat('1', 7 * SlotsPerDay).ToArray();
        var start = localNow.AddHours(-hours);
        for (var quarter = 0; quarter < hours * 2 * 4; quarter++)
        {
            var moment = start.AddMinutes(15 * quarter);
            var weekday = ((int)moment.DayOfWeek + 6) % 7;
            grid[(weekday * SlotsPerDay) + (moment.Hour * 4) + (moment.Minute / 15)] = '0';
        }

        return new string(grid);
    }
}
