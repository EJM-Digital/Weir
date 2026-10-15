using System.Net;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// <c>GET /processing/runtime-settings</c> reports what the server read from its environment when it started. Each case starts a
/// server with its own environment and reads every reported value back, so a variable read into the wrong setting, or kept
/// outside its limits, is caught by a value that differs from the one set.
/// </summary>
[ContractArea("libraries")]
public sealed class ProcessingRuntimeSettingsEnvironmentTests
{
    private const string WorkerCount = "WEIR_PROCESSING_WORKER_COUNT";
    private const string PeriodicEnqueue = "WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_PERIODIC_ENQUEUE_REMUX_JOBS";
    private const string ProbeSize = "WEIR_PROCESSING_PROBE_SIZE_MB";
    private const string AnalyzeDuration = "WEIR_PROCESSING_ANALYZE_DURATION_SECONDS";
    private const string MovieCleanupAge = "WEIR_PROCESSING_MOVIE_OUTPUT_CLEANUP_MIN_AGE_SECONDS";
    private const string TvCleanupAge = "WEIR_PROCESSING_TV_OUTPUT_CLEANUP_MIN_AGE_SECONDS";
    private const string MovieSweep = "WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_ENABLED";
    private const string MovieSweepInterval = "WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_INTERVAL_SECONDS";
    private const string TvSweep = "WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_ENABLED";
    private const string TvSweepInterval = "WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_INTERVAL_SECONDS";
    private const string StaleAge = "WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MIN_STALE_AGE_SECONDS";

    /// <summary>The field that reports each variable.</summary>
    private static readonly Dictionary<string, string> ReportedAs = new()
    {
        [WorkerCount] = "in_process_processing_worker_count",
        [PeriodicEnqueue] = "processing_watched_folder_remux_scan_dispatch_periodic_enqueue_remux_jobs",
        [ProbeSize] = "processing_probe_size_mb",
        [AnalyzeDuration] = "processing_analyze_duration_seconds",
        [MovieCleanupAge] = "processing_movie_output_cleanup_min_age_seconds",
        [TvCleanupAge] = "processing_tv_output_cleanup_min_age_seconds",
        [MovieSweep] = "processing_work_temp_stale_sweep_movie_schedule_enabled",
        [MovieSweepInterval] = "processing_work_temp_stale_sweep_movie_schedule_interval_seconds",
        [TvSweep] = "processing_work_temp_stale_sweep_tv_schedule_enabled",
        [TvSweepInterval] = "processing_work_temp_stale_sweep_tv_schedule_interval_seconds",
        [StaleAge] = "processing_work_temp_stale_sweep_min_stale_age_seconds",
    };

    /// <summary>
    /// Each case sets every variable and says what the server must report for it. Every variable carries a value of its own and
    /// the movie and TV settings differ, so a variable read into the wrong setting shows. The last two cases hold a value to the
    /// ends of its range.
    /// </summary>
    private static readonly Dictionary<string, (string Variable, string Value, string Reported)[]> Cases = new()
    {
        ["values_in_range"] =
        [
            (WorkerCount, "3", "3"), (PeriodicEnqueue, "1", "true"), (ProbeSize, "64", "64"), (AnalyzeDuration, "45", "45"),
            (MovieCleanupAge, "7200", "7200"), (TvCleanupAge, "10800", "10800"),
            (MovieSweep, "1", "true"), (MovieSweepInterval, "900", "900"), (TvSweep, "0", "false"), (TvSweepInterval, "1800", "1800"),
            (StaleAge, "7200", "7200"),
        ],
        ["the_other_scope_on"] =
        [
            (WorkerCount, "5", "5"), (PeriodicEnqueue, "0", "false"), (ProbeSize, "128", "128"), (AnalyzeDuration, "90", "90"),
            (MovieCleanupAge, "86400", "86400"), (TvCleanupAge, "43200", "43200"),
            (MovieSweep, "0", "false"), (MovieSweepInterval, "3600", "3600"), (TvSweep, "1", "true"), (TvSweepInterval, "7200", "7200"),
            (StaleAge, "3600", "3600"),
        ],
        ["below_the_limits"] =
        [
            (WorkerCount, "-5", "1"), (PeriodicEnqueue, "0", "false"), (ProbeSize, "0", "1"), (AnalyzeDuration, "0", "1"),
            (MovieCleanupAge, "1", "3600"), (TvCleanupAge, "1", "3600"),
            (MovieSweep, "1", "true"), (MovieSweepInterval, "1", "60"), (TvSweep, "1", "true"), (TvSweepInterval, "1", "60"),
            (StaleAge, "1", "60"),
        ],
        ["above_the_limits"] =
        [
            (WorkerCount, "99", "10"), (PeriodicEnqueue, "1", "true"), (ProbeSize, "5000", "1024"), (AnalyzeDuration, "9999", "300"),
            (MovieCleanupAge, "999999999", "2592000"), (TvCleanupAge, "999999999", "2592000"),
            (MovieSweep, "1", "true"), (MovieSweepInterval, "999999999", "604800"), (TvSweep, "1", "true"), (TvSweepInterval, "999999999", "604800"),
            (StaleAge, "999999999", "2592000"),
        ],
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task A_value_set_in_the_environment_is_reported_within_its_limits(string name)
    {
        var settings = Cases[name];
        Assert.Equal(ReportedAs.Keys.Order(StringComparer.Ordinal), settings.Select(setting => setting.Variable).Order(StringComparer.Ordinal));
        await using var server = await WeirServer.StartNewAsync(settings.ToDictionary(setting => setting.Variable, setting => setting.Value));
        using var admin = await server.CreateAdminClientAsync();

        var response = await admin.GetAsync(Api + "/processing/runtime-settings");

        response.ShouldBe(HttpStatusCode.OK);
        foreach (var (variable, value, reported) in settings)
        {
            var field = ReportedAs[variable];
            Assert.True(
                response.Fields[field]?.ToJsonString() == reported,
                $"{variable}={value} should be reported as {field}={reported}, got {response.Fields[field]?.ToJsonString()}.");
        }
    }

    [Fact]
    public async Task Without_a_variable_the_documented_default_is_reported()
    {
        // The contract suite's own quiet settings (no workers, no sweeps, no periodic queueing) are set for every server; the rest are untouched.
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();

        var body = (await admin.GetAsync(Api + "/processing/runtime-settings")).Fields;

        Assert.Equal(10, (int)body["processing_probe_size_mb"]!);
        Assert.Equal(10, (int)body["processing_analyze_duration_seconds"]!);
        Assert.Equal(48 * 3600, (int)body["processing_movie_output_cleanup_min_age_seconds"]!);
        Assert.Equal(48 * 3600, (int)body["processing_tv_output_cleanup_min_age_seconds"]!);
        Assert.Equal(3600, (int)body["processing_work_temp_stale_sweep_movie_schedule_interval_seconds"]!);
        Assert.Equal(3600, (int)body["processing_work_temp_stale_sweep_tv_schedule_interval_seconds"]!);
        Assert.Equal(86_400, (int)body["processing_work_temp_stale_sweep_min_stale_age_seconds"]!);
    }

    [Fact]
    public async Task The_notes_name_the_variables_that_set_the_values_and_say_a_restart_is_needed()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();

        var body = (await admin.GetAsync(Api + "/processing/runtime-settings")).Fields;

        Assert.Contains(WorkerCount, (string)body["worker_mode_summary"]!, StringComparison.Ordinal);
        Assert.Contains(MovieCleanupAge, (string)body["movie_output_cleanup_configuration_note"]!, StringComparison.Ordinal);
        Assert.Contains(TvCleanupAge, (string)body["tv_output_cleanup_configuration_note"]!, StringComparison.Ordinal);
        var scanNote = (string)body["watched_folder_scan_periodic_configuration_note"]!;
        foreach (var variable in new[] { PeriodicEnqueue, ProbeSize, AnalyzeDuration })
        {
            Assert.Contains(variable, scanNote, StringComparison.Ordinal);
        }

        foreach (var note in new[] { "movie_output_cleanup_configuration_note", "tv_output_cleanup_configuration_note", "watched_folder_scan_periodic_configuration_note", "work_temp_stale_sweep_periodic_configuration_note" })
        {
            Assert.Contains("restart", (string)body[note]!, StringComparison.OrdinalIgnoreCase);
        }
    }
}
