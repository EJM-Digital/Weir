using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Scheduling;

namespace Weir.Infrastructure.SystemReadings;

/// <summary>
/// Keeps the System view's numbers moving. While a browser holds the live stream open it takes a reading every second;
/// with nobody watching it takes one every ten, only so the traces are already full the moment someone opens the screen.
/// Drives and the work Weir is set up to do are read every thirty seconds, which is as often as they change.
/// </summary>
public sealed partial class SystemStatsSampler : BackgroundService
{
    public static readonly TimeSpan WatchedInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan UnwatchedInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DriveInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often the loop looks at the clock; fine enough to move from the slow pace to the fast one within a second of a browser opening.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>The first readings come quickly even with nobody watching, so the traces are not a single point for ten seconds.</summary>
    private const int ReadingsTakenQuickly = 2;

    private readonly SystemStatsCollector _collector;
    private readonly SystemStatsStore _store;
    private readonly MachineFactsReader _machineFacts;
    private readonly DriveReader _drives;
    private readonly IWorkSource _work;
    private readonly ActivityStreamClients _clients;
    private readonly TimeProvider _time;
    private readonly ILogger<SystemStatsSampler> _logger;
    private DateTimeOffset _lastSampleAt;
    private DateTimeOffset _lastDriveReadAt;
    private int _samplesTaken;
    private WorkSetup _setup;

    public SystemStatsSampler(
        SystemStatsCollector collector,
        SystemStatsStore store,
        MachineFactsReader machineFacts,
        DriveReader drives,
        IWorkSource work,
        ActivityStreamClients clients,
        TimeProvider time,
        int slotsBeforeSettingsRead,
        ILogger<SystemStatsSampler> logger)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(machineFacts);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _collector = collector;
        _store = store;
        _machineFacts = machineFacts;
        _drives = drives;
        _work = work;
        _clients = clients;
        _time = time;
        _logger = logger;
        _setup = new WorkSetup([], slotsBeforeSettingsRead);
    }

    /// <summary>
    /// Takes the first reading before the server starts answering, so the first request already has a reading to give. Its
    /// rates are empty: they need a second reading to compare with.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        TakeReading();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StoppingWork.RunAsync(
            async () =>
            {
                while (true)
                {
                    await RunDueWorkAsync(stoppingToken).ConfigureAwait(false);
                    await Task.Delay(Tick, _time, stoppingToken).ConfigureAwait(false);
                }
            },
            stoppingToken).ConfigureAwait(false);
    }

    /// <summary>One turn of the loop: whatever is due, taken in order. A reading that fails leaves a gap in the traces and is tried again next turn.</summary>
    public async Task RunDueWorkAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_lastDriveReadAt == default || now - _lastDriveReadAt >= DriveInterval)
        {
            await ReadDrivesAsync(now, cancellationToken).ConfigureAwait(false);
        }

        if (IsReadingDue(now))
        {
            TakeReading();
        }
    }

    private bool IsReadingDue(DateTimeOffset now)
    {
        var interval = _clients.Count > 0 || _samplesTaken < ReadingsTakenQuickly ? WatchedInterval : UnwatchedInterval;
        return now - _lastSampleAt >= interval;
    }

    internal void TakeReading()
    {
        try
        {
            _store.Add(_collector.Collect(_setup.Slots));
            _store.SetMachine(_machineFacts.Read());
            _lastSampleAt = _time.GetUtcNow();
            _samplesTaken++;
        }
#pragma warning disable CA1031 // A reading that fails is a gap in a chart, never a reason to stop sampling.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogReadingSkipped(_logger, exception);
        }
    }

    private async Task ReadDrivesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _lastDriveReadAt = now;
        try
        {
            _setup = await _work.ReadAsync(cancellationToken).ConfigureAwait(false);
            _store.SetDrives(await BlockingWork.RunAsync(() => _drives.Read(_setup.Folders)).ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The drives are only for a screen: whatever goes wrong reading them is logged and tried again, never a reason to stop Weir.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogDrivesSkipped(_logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "A System reading was skipped.")]
    private static partial void LogReadingSkipped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Weir could not read its workflows or their drives; the System view keeps the last drives it had.")]
    private static partial void LogDrivesSkipped(ILogger logger, Exception exception);
}
