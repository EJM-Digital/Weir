using Weir.Core.Json;
using Weir.Core.Jobs;
using Weir.Core.Settings;

namespace Weir.Core.Tests.Jobs;

/// <summary>The last edges of the schedule, admission, files-at-once and job-wire rules: short day names, a library running one file, a switched-off or unknown workflow.</summary>
public sealed class JobsRemainingEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Monday,Tuesday")]
    [InlineData("mon,tue")]
    [InlineData(" Mon , Tuesday ")]
    public void Day_names_are_read_by_their_first_three_letters_whatever_their_length(string days)
    {
        var grid = ScheduleGrid.FromDaysAndTimes(days, "09:00", "10:00");

        Assert.True(ScheduleGrid.Allows(grid, "UTC", new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero)));
        Assert.True(ScheduleGrid.Allows(grid, "UTC", new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero)));
        Assert.False(ScheduleGrid.Allows(grid, "UTC", new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void A_window_with_no_times_covers_the_whole_day(string? start, string? end)
    {
        var grid = ScheduleGrid.FromDaysAndTimes("Mon", start, end);

        Assert.True(ScheduleGrid.Allows(grid, "UTC", new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(ScheduleGrid.Allows(grid, "UTC", new DateTimeOffset(2026, 9, 21, 23, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_next_open_slot_is_found_when_the_only_open_slot_is_the_current_one()
    {
        var grid = ScheduleGrid.FromDaysAndTimes("Mon", "12:00", "12:00");

        var next = ScheduleGrid.NextOpenSlot(grid, "UTC", Now);

        Assert.Equal(Now.AddDays(7), next);
    }

    [Fact]
    public void Nothing_is_blocked_when_nothing_is_paused()
    {
        var running = new WorkAdmission(new PauseState(false, null, false), new HashSet<long>());

        Assert.False(running.BlocksDetection);
        Assert.False(running.BlocksProcessing);
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("Mon", null, null, true)]
    [InlineData("Mon", "13:00", null, false)]
    [InlineData("Mon", null, "11:00", false)]
    public void A_workflow_limited_to_hours_with_missing_times_uses_the_whole_day_for_each(string? days, string? start, string? end, bool open)
    {
        var library = new LibraryAdmissionSnapshot(1, true, true, null, true, days, start, end, 0);

        Assert.Equal(open, WorkAdmissionRules.LibraryWindowOpen(library, "UTC", Now));
    }

    [Theory]
    [InlineData("""{"n": "7"}""")]
    [InlineData("""{"n": 7.5}""")]
    [InlineData("""{"n": 1e3}""")]
    [InlineData("""{"n": 99999999999999999999}""")]
    public void A_payload_number_that_is_not_a_whole_number_the_database_holds_is_not_an_integer(string json)
    {
        Assert.Null(JobPayload.StrictInteger(JobPayload.ParseObject(json), "n"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_job_goes_out_with_its_lease_when_it_has_one(bool leased)
    {
        var at = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var job = new ProcessingJob(5, "key", "processing.file", null, ProcessingJobStatus.Completed, leased ? "worker" : null, leased ? at : null, 1, 3, null, null, 0, 0, at, at);

        var wire = ProcessingJobWire.Out(job);

        Assert.Equal(leased, wire.Get("lease_expires_at") is WireString);
        Assert.IsType<WireNull>(wire.Get("payload_json"));
    }

    private static FilesAtOnceReadout Describe(IReadOnlySet<long> blocked, IReadOnlyList<FilesAtOnceLibrary> libraries, IReadOnlyDictionary<long, int> runningPerLibrary, params long?[] waitingLibraries)
    {
        var admission = new WorkAdmission(new PauseState(false, null, true), blocked);
        var waiting = waitingLibraries.Select(id => new WaitingJobSnapshot(0, id)).ToList();
        return FilesAtOnceRules.Describe(5, 10, 0, runningPerLibrary, waiting, libraries, admission, budgetEnabled: false);
    }

    [Fact]
    public void A_workflow_running_exactly_one_file_at_its_limit_says_one_file_is_running()
    {
        var libraries = new[] { new FilesAtOnceLibrary(1, "Movies", true, 1) };

        var readout = Describe(new HashSet<long> { 1 }, libraries, new Dictionary<long, int> { [1] = 1 }, 1);

        Assert.Equal(FilesAtOnceRules.LibraryLimit, readout.WaitingFor);
        Assert.Contains("1 file of its own is running", readout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workflow_running_several_files_at_its_limit_says_they_are_running()
    {
        var libraries = new[] { new FilesAtOnceLibrary(1, "Movies", true, 2) };

        var readout = Describe(new HashSet<long> { 1 }, libraries, new Dictionary<long, int> { [1] = 2 }, 1);

        Assert.Contains("2 files of its own are running", readout.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, true, "waiting for Movies's schedule to open")]
    [InlineData(1, false, "waiting because Movies is switched off")]
    [InlineData(9, true, "waiting for a workflow that is switched off or outside its schedule")]
    public void A_file_held_by_a_closed_workflow_says_why(long blockedId, bool enabled, string expected)
    {
        var libraries = new[] { new FilesAtOnceLibrary(1, "Movies", enabled, 0) };

        var readout = Describe(new HashSet<long> { blockedId }, libraries, new Dictionary<long, int>(), blockedId);

        Assert.Equal(FilesAtOnceRules.LibraryClosed, readout.WaitingFor);
        Assert.Contains(expected, readout.Message, StringComparison.Ordinal);
    }
}
