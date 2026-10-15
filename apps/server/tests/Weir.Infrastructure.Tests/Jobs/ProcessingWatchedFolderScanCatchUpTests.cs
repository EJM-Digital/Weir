using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// A scheduled scan that comes late is a fault only when Weir was running and fell behind. When the computer was asleep, a virtual
/// machine was saved or paused, a container was frozen or Weir was not running, the person has nothing to do about it, so the log
/// says it once, plainly, as information. The clock is the fake one and the process's silences are those <see cref="ProcessPulse"/> noted.
/// </summary>
public sealed class ProcessingWatchedFolderScanCatchUpTests : IDisposable
{
    private const long ScanIntervalSeconds = 300;
    private const string ScanKind = "processing.watched_folder.remux_scan_dispatch.v1";

    private readonly StoreFixture _store = new(("WEIR_CREDENTIALS_SECRET", "scan-catch-up-tests-secret"));
    private readonly CapturingLogger<ProcessingWatchedFolderScanDispatchScheduleTask> _log = new();
    private readonly ProcessPulse _pulse;
    private readonly ProcessingWatchedFolderScanDispatchScheduleTask _task;

    public ProcessingWatchedFolderScanCatchUpTests()
    {
        _pulse = new ProcessPulse(_store.Clock);
        _task = new ProcessingWatchedFolderScanDispatchScheduleTask(
            _store.Database, _store.Options, new ProcessingJobStore(_store.Database, _store.Clock), new OperatorSettingsStore(), new LibraryStore(), _store.Clock, _log, pulse: _pulse);
    }

    public void Dispose() => _store.Dispose();

    private async Task ASeededWorkflowAsync()
    {
        var watched = _store.Home.Join("watch");
        var output = _store.Home.Join("out");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        await ProcessingWatchedFolderScanDispatchScheduleTaskTests.CreateLibraryAsync(_store, watched, output, ScanIntervalSeconds);
    }

    /// <summary>Weir is up and its clock moves, one second at a time, while the scheduler does not look.</summary>
    private void WeirRunsFor(TimeSpan time)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < time; elapsed += TimeSpan.FromSeconds(1))
        {
            _store.Clock.Advance(TimeSpan.FromSeconds(1));
            _pulse.Note();
        }
    }

    private Task FinishTheQueuedScanAsync() => _store.Execute($"UPDATE jobs SET status = 'completed' WHERE job_kind = '{ScanKind}'");

    private Task<long> ScansQueuedAsync() => _store.Scalar($"SELECT COUNT(*) FROM jobs WHERE job_kind = '{ScanKind}'");

    private IEnumerable<string> Said(LogLevel level, string? workflow = null) =>
        _log.Messages.Where(message => message.Level == level && (workflow is null || message.Message.Contains($"workflow {workflow} ", StringComparison.Ordinal))).Select(message => message.Message);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_computer_that_was_asleep_is_told_as_information_with_one_catch_up_scan(bool processNotedItsWakingFirst)
    {
        await ASeededWorkflowAsync();
        await _task.RunOnceAsync(CancellationToken.None);
        await FinishTheQueuedScanAsync();
        WeirRunsFor(TimeSpan.FromSeconds(5));

        _store.Clock.Advance(TimeSpan.FromHours(7));
        if (processNotedItsWakingFirst)
        {
            _pulse.Note();
        }

        await _task.RunOnceAsync(CancellationToken.None);

        Assert.Empty(Said(LogLevel.Warning));
        Assert.Empty(Said(LogLevel.Error));
        var told = Assert.Single(Said(LogLevel.Information, "Movies"));
        Assert.Single(Said(LogLevel.Information, "TV"));
        Assert.Equal(
            "Weir was not running, or the computer was asleep, for about 7 hours, so the scheduled scans of workflow Movies were missed; one catch-up scan is queued.",
            told);
        Assert.Equal(2, await ScansQueuedAsync());
    }

    [Fact]
    public async Task A_scheduler_that_fell_behind_while_weir_ran_is_a_warning()
    {
        await ASeededWorkflowAsync();
        await _task.RunOnceAsync(CancellationToken.None);
        await FinishTheQueuedScanAsync();

        WeirRunsFor(TimeSpan.FromSeconds(1000));
        await _task.RunOnceAsync(CancellationToken.None);

        Assert.Empty(Said(LogLevel.Information));
        var warned = Assert.Single(Said(LogLevel.Warning, "Movies"));
        Assert.Equal(
            "The scheduled scan of workflow Movies started 11 minutes late while Weir was running, so 2 scheduled scans were missed; one catch-up scan is queued.",
            warned);
        Assert.Equal(2, await ScansQueuedAsync());
    }

    [Fact]
    public async Task A_short_sleep_inside_a_long_delay_is_still_a_warning()
    {
        await ASeededWorkflowAsync();
        await _task.RunOnceAsync(CancellationToken.None);
        await FinishTheQueuedScanAsync();

        WeirRunsFor(TimeSpan.FromSeconds(500));
        _store.Clock.Advance(TimeSpan.FromSeconds(30));
        WeirRunsFor(TimeSpan.FromSeconds(500));
        await _task.RunOnceAsync(CancellationToken.None);

        Assert.Empty(Said(LogLevel.Information));
        Assert.Single(Said(LogLevel.Warning, "Movies"));
    }

    [Fact]
    public async Task Without_a_pulse_a_late_scan_is_a_warning()
    {
        await ASeededWorkflowAsync();
        var task = new ProcessingWatchedFolderScanDispatchScheduleTask(
            _store.Database, _store.Options, new ProcessingJobStore(_store.Database, _store.Clock), new OperatorSettingsStore(), new LibraryStore(), _store.Clock, _log);
        await task.RunOnceAsync(CancellationToken.None);
        await FinishTheQueuedScanAsync();

        _store.Clock.Advance(TimeSpan.FromHours(2));
        await task.RunOnceAsync(CancellationToken.None);

        Assert.Single(Said(LogLevel.Warning, "Movies"));
    }

    [Fact]
    public async Task The_first_start_after_a_long_stop_has_nothing_to_catch_up_and_says_nothing_wrong()
    {
        await ASeededWorkflowAsync();

        await _task.RunOnceAsync(CancellationToken.None);

        Assert.Empty(Said(LogLevel.Warning));
        Assert.Empty(Said(LogLevel.Error));
        Assert.Equal(1, await ScansQueuedAsync());
    }

    [Fact]
    public async Task Ticks_on_time_say_nothing()
    {
        await ASeededWorkflowAsync();
        await _task.RunOnceAsync(CancellationToken.None);
        await FinishTheQueuedScanAsync();

        for (var second = 0; second < 650; second++)
        {
            WeirRunsFor(TimeSpan.FromSeconds(1));
            await _task.RunOnceAsync(CancellationToken.None);
            await FinishTheQueuedScanAsync();
        }

        Assert.Empty(_log.Messages);
        Assert.True(await ScansQueuedAsync() >= 2);
    }
}
