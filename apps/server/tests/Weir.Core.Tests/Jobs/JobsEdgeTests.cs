using System.Text.Json;
using Weir.Core.Jobs;
using Weir.Core.Observability;
using Weir.Core.Settings;

namespace Weir.Core.Tests.Jobs;

/// <summary>Schedules, admission and job payloads at their edges: unreadable times, missing settings, and payload fields of the wrong kind.</summary>
public sealed class JobsEdgeTests
{
    // A Monday.
    private static readonly DateTimeOffset MondayNoon = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Monday = ScheduleGrid.DayNames[0];

    private static LibraryAdmissionSnapshot Library(
        long id,
        bool enabled = true,
        bool scheduleEnabled = false,
        string? grid = null,
        bool hoursLimited = false,
        string? days = null,
        string? start = null,
        string? end = null,
        long limit = 0) =>
        new(id, enabled, scheduleEnabled, grid, hoursLimited, days, start, end, limit);

    [Theory]
    [InlineData("22:00", "06:00", 23, true)]
    [InlineData("22:00", "06:00", 3, true)]
    [InlineData("22:00", "06:00", 12, false)]
    [InlineData("09:00", "17:00", 12, true)]
    [InlineData("09:00", "17:00", 20, false)]
    public void A_window_that_ends_before_it_starts_runs_past_midnight(string start, string end, int hour, bool open)
    {
        var now = new DateTimeOffset(2026, 9, 21, hour, 0, 0, TimeSpan.Zero);

        Assert.Equal(open, ScheduleWallClock.TimeWindowActive(true, Monday, start, end, "UTC", now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9")]
    [InlineData("aa:30")]
    [InlineData("09:bb")]
    [InlineData("24:00")]
    [InlineData("09:60")]
    public void A_start_time_that_cannot_be_read_is_midnight(string? start)
    {
        // Open at 00:30 only if the unreadable start fell back to 00:00.
        var now = new DateTimeOffset(2026, 9, 21, 0, 30, 0, TimeSpan.Zero);

        Assert.True(ScheduleWallClock.TimeWindowActive(true, Monday, start, "01:00", "UTC", now));
    }

    [Fact]
    public void A_schedule_with_no_days_is_never_open()
    {
        Assert.False(ScheduleWallClock.TimeWindowActive(true, null, "00:00", "23:59", "UTC", MondayNoon));
    }

    [Fact]
    public void Days_that_are_not_day_names_mean_every_day()
    {
        Assert.True(ScheduleWallClock.TimeWindowActive(true, "someday", "00:00", "23:59", "UTC", MondayNoon));
    }

    [Fact]
    public void No_days_at_all_is_no_grid()
    {
        Assert.Equal(string.Empty, ScheduleGrid.FromDaysAndTimes(null, "09:00", "17:00"));
    }

    [Fact]
    public void A_grid_with_nothing_open_has_no_next_slot()
    {
        Assert.Null(ScheduleGrid.NextOpenSlot(new string('0', ScheduleGrid.SlotsPerWeek), "UTC", MondayNoon));
    }

    [Theory]
    [InlineData("99999999999:00")]
    [InlineData("12:-99999999999")]
    public void A_time_with_a_number_too_big_for_a_clock_is_not_a_time(string text)
    {
        Assert.Equal(string.Empty, ScheduleGrid.FromDaysAndTimes(Monday, text, "17:00"));
    }

    [Theory]
    [InlineData("a/b/", "b")]
    [InlineData("name", "name")]
    public void The_last_part_of_a_path_is_its_base_name(string path, string expected)
    {
        Assert.Equal(expected, WeirTempFiles.BaseName(path));
    }

    [Theory]
    [InlineData(".x.partial", false, true)]
    [InlineData(".X.PARTIAL", true, true)]
    [InlineData(".X.PARTIAL", false, false)]
    [InlineData(".partial", false, false)]
    public void A_hidden_name_ending_in_partial_is_a_partial_output(string name, bool ignoreCase, bool expected)
    {
        Assert.Equal(expected, WeirTempFiles.IsPartialOutputName(name, ignoreCase));
    }

    [Fact]
    public void A_cost_table_with_no_1080p_costs_nothing_for_a_resolution_it_does_not_know()
    {
        var budget = new RunnerBudget(4, new Dictionary<string, int>());

        Assert.Equal(0, budget.CostFor("8k"));
    }

    [Fact]
    public void Scanning_while_paused_keeps_detection_but_not_processing_running()
    {
        var admission = new WorkAdmission(new PauseState(true, null, ScanWhilePaused: true), new HashSet<long>());

        Assert.True(admission.BlocksProcessing);
        Assert.False(admission.BlocksDetection);
    }

    [Fact]
    public void A_window_limited_to_hours_with_no_times_given_is_open_all_day()
    {
        var library = Library(1, scheduleEnabled: true, hoursLimited: true, days: Monday);

        Assert.True(WorkAdmissionRules.LibraryWindowOpen(library, "UTC", MondayNoon));
    }

    [Fact]
    public void A_schedule_with_no_hour_limit_and_no_grid_is_always_open()
    {
        Assert.True(WorkAdmissionRules.LibraryWindowOpen(Library(1, scheduleEnabled: true), "UTC", MondayNoon));
    }

    [Fact]
    public void A_library_with_no_grid_has_no_time_it_reopens()
    {
        Assert.Null(WorkAdmissionRules.LibraryWindowReopensAt(Library(1), "UTC", MondayNoon));
    }

    [Fact]
    public void Settings_with_no_time_zone_are_judged_in_utc()
    {
        var settings = new SuitePauseSettings(null, ProcessingPaused: false, ProcessingPausedUntil: null, ScanWhilePaused: false);

        var admission = WorkAdmissionRules.Evaluate(settings, null, [], [], MondayNoon);

        Assert.Equal("UTC", admission.TimezoneName);
    }

    [Fact]
    public void A_payload_that_is_not_an_object_has_no_fields()
    {
        Assert.Null(JobPayload.ParseObject(null));
        Assert.Null(JobPayload.ParseObject("[1]"));
        Assert.Null(JobPayload.ParseObject("{broken"));
        Assert.Null(JobPayload.StringProperty(null, "name"));
        Assert.Null(JobPayload.StrictInteger(null, "n"));
        Assert.Null(JobPayload.LooseInteger(null, "n"));
    }

    [Theory]
    [InlineData("""{"n": true}""", 1L)]
    [InlineData("""{"n": false}""", 0L)]
    [InlineData("""{"n": 7}""", 7L)]
    [InlineData("""{"n": 7.5}""", null)]
    [InlineData("""{"n": "7"}""", null)]
    [InlineData("""{"m": 7}""", null)]
    public void A_payload_integer_may_be_a_true_or_false_only_when_asked_for_loosely(string json, long? expected)
    {
        var payload = JobPayload.ParseObject(json);

        Assert.Equal(expected, JobPayload.LooseInteger(payload, "n"));
    }

    [Fact]
    public void A_payload_integer_read_strictly_is_not_a_true_or_false()
    {
        Assert.Null(JobPayload.StrictInteger(JobPayload.ParseObject("""{"n": true}"""), "n"));
        Assert.Equal(7, JobPayload.StrictInteger(JobPayload.ParseObject("""{"n": 7}"""), "n"));
        Assert.Null(JobPayload.StrictInteger(JobPayload.ParseObject("""{"m": 7}"""), "n"));
        Assert.Equal(JsonValueKind.Object, JobPayload.ParseObject("""{"n": 7}""")!.Value.ValueKind);
    }

    [Fact]
    public void A_failure_with_no_technical_detail_stores_no_detail_line()
    {
        var stored = WorkerFailures.StoredError(new OperatorFailure("jobs", "run", FailureKind.Filesystem, true, "It failed.", "Why.", "Then.", "Try again.", string.Empty));

        Assert.Equal("It failed. Next action: Try again.", stored);
    }

    [Fact]
    public void A_dedupe_key_that_is_missing_still_becomes_a_tombstone()
    {
        Assert.StartsWith(":ran:", JobQueueRules.TombstoneFinishedDedupeKey(null!, 5), StringComparison.Ordinal);
    }
}
