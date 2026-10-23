using System.Globalization;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// The two hourly clean-up timers that read what the owner saved: how long finished jobs are kept (the environment) and how long a
/// gone file's history is kept (the setting on Cleanup). Time is the fake clock's, and the database is a real SQLite file.
/// </summary>
public sealed class RetentionTasksTests : IDisposable
{
    private readonly JobsTestDatabase _db = new(keepSeedRows: true);

    public void Dispose() => _db.Dispose();

    private JobRowsRetentionTask JobRowsTask(params (string Name, string Value)[] variables)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["WEIR_HOME"] = _db.Home };
        foreach (var (name, value) in variables)
        {
            values[name] = value;
        }

        var options = WeirOptionsLoader.Load(new RuntimeEnvironment(values, OperatingSystem.IsWindows(), _db.Home, _db.Home));
        return new JobRowsRetentionTask(new JobRowsRetention(_db.Store), options, _db.Clock, new RecordingLogger<JobRowsRetentionTask>());
    }

    private void FinishedJob(string key, DateTimeOffset updatedAt)
    {
        _db.InsertRawJob(key, "processing.a.v1", ProcessingJobStatus.Completed);
        _db.Execute("UPDATE jobs SET updated_at = @at WHERE dedupe_key = @key", ("@at", updatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), ("@key", key));
    }

    private string KeptJobs() => _db.Scalar("SELECT coalesce(group_concat(dedupe_key, ','), '') FROM (SELECT dedupe_key FROM jobs ORDER BY dedupe_key)")?.ToString() ?? string.Empty;

    [Fact]
    public async Task Finished_jobs_older_than_the_saved_retention_days_are_pruned_and_younger_ones_kept()
    {
        FinishedJob("two-days-old", _db.Clock.GetUtcNow().AddDays(-2));
        FinishedJob("half-a-day-old", _db.Clock.GetUtcNow().AddHours(-12));
        var task = JobRowsTask(("WEIR_JOB_ROWS_RETENTION_DAYS", "1"));

        await task.RunOnceAsync(CancellationToken.None);

        Assert.Equal("half-a-day-old", KeptJobs());
    }

    [Fact]
    public async Task The_default_retention_keeps_a_job_the_one_day_setting_would_prune()
    {
        FinishedJob("two-days-old", _db.Clock.GetUtcNow().AddDays(-2));
        var task = JobRowsTask();

        await task.RunOnceAsync(CancellationToken.None);

        Assert.Equal("two-days-old", KeptJobs());
    }

    [Fact]
    public void The_timer_takes_its_interval_from_the_environment_and_prunes_when_it_starts()
    {
        var task = JobRowsTask(("WEIR_JOB_ROWS_RETENTION_SCHEDULE_INTERVAL_SECONDS", "600"));

        Assert.Equal(TimeSpan.FromSeconds(600), task.Interval);
        Assert.True(task.RunAtStart);
        Assert.Equal(TimeSpan.FromHours(1), JobRowsTask().Interval);
        Assert.Equal(TimeSpan.FromSeconds(60), JobRowsTask(("WEIR_JOB_ROWS_RETENTION_SCHEDULE_INTERVAL_SECONDS", "1")).Interval);
    }

    [Fact]
    public async Task A_run_that_pruned_something_says_how_many_and_a_quiet_run_says_nothing()
    {
        var logger = new RecordingLogger<JobRowsRetentionTask>();
        var options = WeirOptionsLoader.Load(new RuntimeEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WEIR_HOME"] = _db.Home, ["WEIR_JOB_ROWS_RETENTION_DAYS"] = "1" },
            OperatingSystem.IsWindows(),
            _db.Home,
            _db.Home));
        var task = new JobRowsRetentionTask(new JobRowsRetention(_db.Store), options, _db.Clock, logger);

        await task.RunOnceAsync(CancellationToken.None);
        Assert.Empty(logger.At(Microsoft.Extensions.Logging.LogLevel.Information));

        FinishedJob("old", _db.Clock.GetUtcNow().AddDays(-3));
        await task.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["History retention pruned processing jobs=1 activity events=0"], logger.At(Microsoft.Extensions.Logging.LogLevel.Information));
    }

    private FileLogRetentionTask FileLogTask() =>
        new(_db.Database, new OperatorSettingsStore(), new FileLogStore(), _db.Clock, new RecordingLogger<FileLogRetentionTask>());

    private void GoneFilesHistory(string path)
    {
        _db.Execute("INSERT OR IGNORE INTO libraries (id, name, media_type, watched_folder, display_order) VALUES (1, 'Movies', 'movie', '/in', 1)");
        _db.Execute(
            "INSERT INTO file_logs (library_id, relative_path, recorded_at) VALUES (1, @path, @recorded)",
            ("@path", path), ("@recorded", _db.Clock.GetUtcNow().AddDays(-90).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)));
    }

    private long HistoryRows() => _db.Count("SELECT count(*) FROM file_logs");

    [Fact]
    public async Task A_gone_files_history_goes_after_the_saved_number_of_days_and_not_before()
    {
        _db.Execute("UPDATE operator_settings SET file_log_retention_days = 10");
        GoneFilesHistory("film.mkv");
        var task = FileLogTask();

        await task.RunOnceAsync(CancellationToken.None);
        _db.Clock.Advance(TimeSpan.FromDays(9));
        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, HistoryRows());

        _db.Clock.Advance(TimeSpan.FromDays(2));
        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, HistoryRows());
    }

    [Fact]
    public async Task A_changed_setting_is_the_one_the_next_run_uses()
    {
        _db.Execute("UPDATE operator_settings SET file_log_retention_days = 30");
        GoneFilesHistory("film.mkv");
        var task = FileLogTask();
        await task.RunOnceAsync(CancellationToken.None);
        _db.Clock.Advance(TimeSpan.FromDays(15));
        await task.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, HistoryRows());

        _db.Execute("UPDATE operator_settings SET file_log_retention_days = 10");
        await task.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, HistoryRows());
    }

    [Fact]
    public async Task Zero_days_keeps_a_gone_files_history_for_ever()
    {
        _db.Execute("UPDATE operator_settings SET file_log_retention_days = 0");
        GoneFilesHistory("film.mkv");
        var task = FileLogTask();

        await task.RunOnceAsync(CancellationToken.None);
        _db.Clock.Advance(TimeSpan.FromDays(3000));
        await task.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, HistoryRows());
    }

    [Fact]
    public async Task A_run_that_clears_several_records_says_how_many_in_the_plural()
    {
        _db.Execute("UPDATE operator_settings SET file_log_retention_days = 10");
        GoneFilesHistory("one.mkv");
        GoneFilesHistory("two.mkv");
        var logger = new RecordingLogger<FileLogRetentionTask>();

        var task = new FileLogRetentionTask(_db.Database, new OperatorSettingsStore(), new FileLogStore(), _db.Clock, logger);

        await task.RunOnceAsync(CancellationToken.None);
        _db.Clock.Advance(TimeSpan.FromDays(11));
        await task.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["Removed 2 processing records past their retention window."], logger.At(Microsoft.Extensions.Logging.LogLevel.Information));
    }
}
