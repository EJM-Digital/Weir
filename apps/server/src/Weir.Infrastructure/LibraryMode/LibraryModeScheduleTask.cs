using Microsoft.Extensions.Logging;
using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>When one library's scheduled scan and clean next runs, from what is stored; shared by the timer and the API.</summary>
public static class LibraryModeScheduling
{
    /// <summary>
    /// Null when it will not run: the schedule is off, the library has no library folders to scan, the library is
    /// switched off, or its window never opens. May be in the past, which means it is due now.
    /// </summary>
    public static async Task<DateTimeOffset?> NextRunAsync(
        UnitOfWork uow, SuiteSettingsStore suiteSettings, LibraryScanStore scans, ProcessingLibraryRecord library, LibrarySettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(suiteSettings);
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.ScheduleEnabled || settings.Folders.Count == 0)
        {
            return null;
        }

        var suite = await suiteSettings.GetAsync(uow).ConfigureAwait(false);
        var timezone = suite?.AppTimezone is { } zone && !string.IsNullOrWhiteSpace(zone) ? zone.Trim() : "UTC";
        var last = await scans.LastScheduledRunAtAsync(uow, library.Id).ConfigureAwait(false);
        var window = new LibraryAdmissionSnapshot(
            library.Id, library.Enabled, library.ScheduleEnabled, library.ScheduleGrid, library.ScheduleHoursLimited,
            library.ScheduleDays, library.ScheduleStart, library.ScheduleEnd, library.MaxConcurrentFiles);
        return LibraryModeSchedule.NextRunAt(last, window, timezone, now);
    }
}

/// <summary>
/// <c>processing-library-mode-schedule</c>: runs library mode's "Scheduled scan and clean" for every library whose
/// schedule switch is on.
/// </summary>
/// <remarks>
/// Every half minute it asks each library whether its scheduled run is due (<see cref="LibraryModeScheduling"/>) and, when
/// it is, queues a scan with the <see cref="LibraryModeSchedule.Trigger"/> trigger. The scan queues the clean itself when it
/// finishes (<see cref="LibraryScanHandler"/>), because only then does Weir know what the files hold. A library already
/// being scanned is left to finish first. The scan and every clean are ordinary library jobs, so the worker still only
/// starts them while the library's window is open and processing is not paused.
/// </remarks>
public sealed partial class LibraryModeScheduleTask : IPeriodicTask
{
    /// <summary>How late a run may start and still count as the one that was due, keeping the next a day after it.</summary>
    private static readonly TimeSpan OnTime = TimeSpan.FromMinutes(5);

    private readonly SqliteDatabase _database;
    private readonly ProcessingJobStore _jobs;
    private readonly LibraryScanStore _scans;
    private readonly LibrarySettingsStore _librarySettings;
    private readonly LibraryStore _libraries;
    private readonly TimeProvider _time;
    private readonly SuiteSettingsStore _suiteSettings;
    private readonly ILogger<LibraryModeScheduleTask> _logger;
    private readonly PeriodicTaskRegistry? _tasks;
    private readonly HashSet<long> _announced = [];

    public LibraryModeScheduleTask(
        SqliteDatabase database,
        ProcessingJobStore jobs,
        LibraryScanStore scans,
        LibrarySettingsStore librarySettings,
        LibraryStore libraries,
        TimeProvider time,
        SuiteSettingsStore suiteSettings,
        ILogger<LibraryModeScheduleTask> logger,
        PeriodicTaskRegistry? tasks = null)
    {
        _tasks = tasks;
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(librarySettings);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(suiteSettings);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _jobs = jobs;
        _scans = scans;
        _librarySettings = librarySettings;
        _libraries = libraries;
        _time = time;
        _suiteSettings = suiteSettings;
        _logger = logger;
    }

    public string Name => "processing-library-mode-schedule";

    public string? Label => null;

    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public bool RunAtStart => true;

    public TimeSpan? FailureCooldown => null;

    public string FailureMessage => "Library mode's scheduled scan and clean failed to check its workflows.";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var enabled = await _libraries.ListAsync(uow, enabledOnly: true).ConfigureAwait(false);
            foreach (var library in enabled)
            {
                var settings = await _librarySettings.GetAsync(uow, library.Id).ConfigureAwait(false);
                var nextRunAt = await LibraryModeScheduling.NextRunAsync(uow, _suiteSettings, _scans, library, settings, now).ConfigureAwait(false);
                AnnounceNextClean(library, nextRunAt);
                if (nextRunAt is not { } due || due > now)
                {
                    continue;
                }

                if (await _scans.ActiveScanAsync(uow, library.Id).ConfigureAwait(false) is not null)
                {
                    // A scan someone asked for is already under way; the scheduled one follows once it is done.
                    continue;
                }

                // On time, the run keeps the time it was due so the next is exactly a day later; a run that was missed
                // (Weir was off) starts the day again from now rather than queueing one scan per missed day.
                var scheduledAt = now - due <= OnTime ? due : now;
                await _scans.RequestScanAsync(uow, _jobs, library.Id, LibraryModeSchedule.Trigger, scheduledAt).ConfigureAwait(false);
                LogScheduledScanQueued(library.Name);
                AnnounceNextClean(library, scheduledAt + LibraryModeSchedule.Interval);
            }

            ForgetDepartedLibraries(enabled.Select(library => library.Id));
            await uow.CommitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The task list shows a workflow's scheduled scan and clean while its schedule can run, and drops it otherwise.</summary>
    private void AnnounceNextClean(ProcessingLibraryRecord library, DateTimeOffset? nextRunAt)
    {
        if (_tasks is null)
        {
            return;
        }

        if (nextRunAt is { } at)
        {
            _tasks.Plan(ScheduledJobTasks.LibraryCleanKey(library.Id), ScheduledJobTasks.LibraryCleanLabel(library.Name), at, LibraryModeSchedule.Interval);
            _announced.Add(library.Id);
        }
        else
        {
            _tasks.Remove(ScheduledJobTasks.LibraryCleanKey(library.Id));
            _announced.Remove(library.Id);
        }
    }

    private void ForgetDepartedLibraries(IEnumerable<long> enabledIds)
    {
        if (_tasks is null)
        {
            return;
        }

        foreach (var id in _announced.Except(enabledIds).ToList())
        {
            _tasks.Remove(ScheduledJobTasks.LibraryCleanKey(id));
            _announced.Remove(id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled scan and clean: checking {Library} now.")]
    private partial void LogScheduledScanQueued(string library);
}
