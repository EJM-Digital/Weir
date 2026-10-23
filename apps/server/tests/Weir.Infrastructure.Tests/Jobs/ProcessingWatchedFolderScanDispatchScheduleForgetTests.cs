using Microsoft.Extensions.Logging;
using Weir.Core.Jobs;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>What the periodic scheduler leaves behind in the wake-up book and the task list when a workflow stops being scheduled, and what a failed tick costs.</summary>
public sealed class ProcessingWatchedFolderScanDispatchScheduleForgetTests : IDisposable
{
    private static readonly OperatorSettingsStore OperatorSettings = new();
    private static readonly LibraryStore Libraries = new();

    private readonly StoreFixture _store = new(("WEIR_CREDENTIALS_SECRET", "schedule-forget-secret"));
    private readonly ScanWakeups _wakeups = new();
    private readonly ScanSettingsChanges _changes = new();
    private readonly PeriodicTaskRegistry _tasks;
    private readonly ListLogger<ProcessingWatchedFolderScanDispatchScheduleTask> _log = new();
    private readonly ProcessingWatchedFolderScanDispatchScheduleTask _task;
    private readonly string _watched;
    private readonly string _output;
    private long _libraryId;

    public ProcessingWatchedFolderScanDispatchScheduleForgetTests()
    {
        _watched = _store.Home.Join("watch");
        _output = _store.Home.Join("out");
        Directory.CreateDirectory(_watched);
        Directory.CreateDirectory(_output);
        _tasks = new PeriodicTaskRegistry(_store.Clock);
        _task = new ProcessingWatchedFolderScanDispatchScheduleTask(
            _store.Database, _store.Options, new ProcessingJobStore(_store.Database, _store.Clock), OperatorSettings, Libraries, _store.Clock, _log,
            _wakeups, _changes, _tasks);
    }

    public void Dispose() => _store.Dispose();

    private async Task ScheduleAsync()
    {
        _libraryId = await ProcessingWatchedFolderScanDispatchScheduleTaskTests.CreateLibraryAsync(_store, _watched, _output, scanIntervalSeconds: 600);
        await _task.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(_wakeups.NextPeriodicFor(_libraryId));
        Assert.Contains(_tasks.Snapshot(), task => task.Key == ScheduledJobTasks.ScanKey(_libraryId));
    }

    private async Task NextTickAsync()
    {
        _changes.Record();
        _store.Clock.Advance(TimeSpan.FromSeconds(1));
        await _task.RunOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_workflow_switched_off_is_dropped_from_the_wake_up_book_and_the_task_list()
    {
        await ScheduleAsync();
        await using (var uow = await UnitOfWork.OpenAsync(_store.Database))
        {
            var library = await Libraries.GetAsync(uow, _libraryId) ?? throw new InvalidOperationException();
            await Libraries.UpdateAsync(uow, library, new ProcessingLibraryInput { Name = library.Name, MediaType = library.MediaType, WatchedFolder = _watched, OutputFolder = _output, Enabled = false });
            await uow.CommitAsync();
        }

        await NextTickAsync();

        Assert.Null(_wakeups.NextPeriodicFor(_libraryId));
        Assert.DoesNotContain(_tasks.Snapshot(), task => task.Key == ScheduledJobTasks.ScanKey(_libraryId));
    }

    [Fact]
    public async Task A_scope_whose_periodic_scans_are_switched_off_is_dropped_from_the_wake_up_book()
    {
        await ScheduleAsync();
        await using (var uow = await UnitOfWork.OpenAsync(_store.Database))
        {
            var row = await OperatorSettings.EnsureAsync(uow);
            await OperatorSettings.UpdateAsync(uow, row, row with { MovieScheduleEnabled = false });
            await uow.CommitAsync();
        }

        await NextTickAsync();

        Assert.Null(_wakeups.NextPeriodicFor(_libraryId));
    }

    [Fact]
    public async Task A_tick_that_fails_is_logged_with_the_workflow_and_the_scan_waits_out_the_cooldown()
    {
        _libraryId = await ProcessingWatchedFolderScanDispatchScheduleTaskTests.CreateLibraryAsync(_store, _watched, _output, scanIntervalSeconds: 600);
        using (var connection = _store.Database.Open())
        {
            using var rename = connection.CreateCommand();
            rename.CommandText = "ALTER TABLE jobs RENAME TO jobs_unavailable";
            rename.ExecuteNonQuery();
        }

        var started = _store.Clock.GetUtcNow();
        await _task.RunOnceAsync(CancellationToken.None);

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Watched-folder scheduler failed for workflow Movie", StringComparison.Ordinal));
        Assert.Equal(started + PeriodicSchedule.FailureCooldown, _wakeups.NextPeriodicFor(_libraryId));
    }
}
