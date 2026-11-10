using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Sets up Weir's workflows from a media manager that reports its folders (Deluno) and keeps them in step with it
/// (<see cref="WorkflowSyncRules"/> decides what changes). For every enabled Deluno connection whose key works, each library
/// set to Refine before import gets one workflow: the one already linked to it, else an unconfigured one of the same media
/// type (the install's Movies and TV), else a new one. Only the watched and output folders (and, when a workflow is made or
/// adopted, its name and link) are written; the work folder, rules profile, schedule and the rest stay the person's. Nothing
/// is ever deleted, and a workflow whose library stops processing with Weir or is removed from the manager is reported and left as it is.
/// </summary>
/// <remarks>
/// A manager that does not answer changes nothing and is tried again next time. The reads never hold the database, and
/// <see cref="RequestSync"/> returns at once, so a request or a hand-off never waits on a manager.
/// </remarks>
public sealed partial class ManagerWorkflowSync : IDisposable
{
    /// <summary>Why a sync runs, as an Activity trigger: a person saved or tested a connection.</summary>
    public const string ManualTrigger = "manual";

    /// <summary>Why a sync runs, as an Activity trigger: the periodic task came round.</summary>
    public const string ScheduledTrigger = "scheduled";

    /// <summary>How often a connection with no workflow yet is looked at again, after the first few quicker looks.</summary>
    internal static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(15);

    /// <summary>How long a connection with no workflow yet is looked at again for; after that the periodic task keeps checking.</summary>
    internal static readonly TimeSpan RetryFor = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan[] QuickRetries = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), RetryEvery];

    private static readonly int RetryLimit =
        QuickRetries.Length + (int)((RetryFor - TimeSpan.FromTicks(QuickRetries.Sum(step => step.Ticks))) / RetryEvery);

    private readonly SqliteDatabase _database;
    private readonly MediaManagerConnectionStore _connectionStore;
    private readonly MediaManagerConnectionService _connections;
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly LibraryStore _libraries;
    private readonly ScanSettingsChanges _scanChanges;
    private readonly WeirOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ManagerWorkflowSync> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private bool _draining;
    private bool _again;
    private CancellationTokenSource? _wake;
    private Task _drain = Task.CompletedTask;

    public ManagerWorkflowSync(
        SqliteDatabase database,
        MediaManagerConnectionStore connectionStore,
        MediaManagerConnectionService connections,
        IManagerHttpHandlerFactory handlers,
        LibraryStore libraries,
        ScanSettingsChanges scanChanges,
        WeirOptions options,
        TimeProvider time,
        ILogger<ManagerWorkflowSync> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _connectionStore = connectionStore ?? throw new ArgumentNullException(nameof(connectionStore));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _scanChanges = scanChanges ?? throw new ArgumentNullException(nameof(scanChanges));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs a sync in the background and returns at once. Requests that arrive while one runs are folded into one more run
    /// after it, so a burst of saves never queues a sync each. A request that arrives while the retries wait ends the wait and
    /// starts them afresh, so there is only ever one loop, whatever the number of connections or requests.
    /// </summary>
    public void RequestSync()
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            if (_draining)
            {
                _again = true;
                _wake?.Cancel();
                return;
            }

            _draining = true;
            _drain = Task.Run(DrainAsync, CancellationToken.None);
        }
    }

    /// <summary>Completes when the background sync and its retries have ended; already complete when none is running.</summary>
    internal Task Drained
    {
        get
        {
            lock (_gate)
            {
                return _drain;
            }
        }
    }

    private async Task DrainAsync()
    {
        var stopping = _stopping.Token;
        var retries = 0;
        HashSet<long>? only = null;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(ManualTrigger, only, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A sync nobody is waiting for must never end the process; the next request or run tries again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSyncFailed(_logger, exception);
            }

            if (TakeRequest())
            {
                (retries, only) = (0, null);
                continue;
            }

            var waiting = await WaitingForWorkflowsAsync(stopping).ConfigureAwait(false);
            if (waiting.Count == 0 || retries >= RetryLimit)
            {
                if (EndUnlessRequested())
                {
                    return;
                }

                (retries, only) = (0, null);
                continue;
            }

            var pause = RetryDelay(retries++);
            foreach (var label in waiting.Select(entry => entry.Connection.Label))
            {
                LogWillLookAgain(_logger, label, (int)pause.TotalSeconds);
            }

            only = [.. waiting.Select(entry => entry.Id)];
            await PauseAsync(pause, stopping).ConfigureAwait(false);
            if (TakeRequest())
            {
                (retries, only) = (0, null);
            }
        }

        lock (_gate)
        {
            _draining = false;
        }
    }

    /// <summary>True when a request came in since the last look, which is then answered by the next pass.</summary>
    private bool TakeRequest()
    {
        lock (_gate)
        {
            var requested = _again;
            _again = false;
            return requested;
        }
    }

    /// <summary>Ends the loop and returns true, unless a request came in meanwhile (then false, and the request is taken).</summary>
    private bool EndUnlessRequested()
    {
        lock (_gate)
        {
            if (_again)
            {
                _again = false;
                return false;
            }

            _draining = false;
            return true;
        }
    }

    /// <summary>Waits <paramref name="pause"/> on the injected clock, or less when a request or the server stopping ends it.</summary>
    private async Task PauseAsync(TimeSpan pause, CancellationToken stopping)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        lock (_gate)
        {
            _wake = wake;
            if (_again)
            {
                wake.Cancel();
            }
        }

        try
        {
            await Task.Delay(pause, _time, wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A new request or the server stopping; the loop reads which.
        }
        finally
        {
            lock (_gate)
            {
                _wake = null;
            }
        }
    }

    private static TimeSpan RetryDelay(int retry) => retry < QuickRetries.Length ? QuickRetries[retry] : RetryEvery;

    /// <summary>The enabled connections that have no workflow yet; none when syncing is switched off or the look fails (the periodic task still checks).</summary>
    private async Task<List<(long Id, ManagerConnection Connection)>> WaitingForWorkflowsAsync(CancellationToken cancellationToken)
    {
        if (!_options.ManagerWorkflowSyncEnabled)
        {
            return [];
        }

        try
        {
            var connections = await SyncingConnectionsAsync(cancellationToken).ConfigureAwait(false);
            var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                var linkedTo = new HashSet<long>();
                foreach (var workflow in await _libraries.ListAsync(uow).ConfigureAwait(false))
                {
                    if (workflow.DiscoveredFromConnectionId is { } discovered)
                    {
                        linkedTo.Add(discovered);
                    }

                    linkedTo.UnionWith(await _libraries.ManagerConnectionIdsAsync(uow, workflow.Id).ConfigureAwait(false));
                }

                return connections.Where(entry => !linkedTo.Contains(entry.Id)).ToList();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
#pragma warning disable CA1031 // The retries are a convenience; a failed look ends them and the periodic task still checks.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogLookFailed(_logger, exception);
            return [];
        }
    }

    /// <summary>One pass over every enabled Deluno connection. <paramref name="trigger"/> is why it runs, as an Activity trigger.</summary>
    public Task SyncAllAsync(string trigger, CancellationToken cancellationToken) => SyncAsync(trigger, null, cancellationToken);

    private async Task SyncAsync(string trigger, HashSet<long>? only, CancellationToken cancellationToken)
    {
        if (!_options.ManagerWorkflowSyncEnabled)
        {
            return;
        }

        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (connectionId, connection) in await SyncingConnectionsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (only is null || only.Contains(connectionId))
                {
                    await SyncConnectionAsync(connectionId, connection, trigger, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<List<(long Id, ManagerConnection Connection)>> SyncingConnectionsAsync(CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var syncing = new List<(long Id, ManagerConnection Connection)>();
            foreach (var row in (await _connectionStore.ListEnabledAsync(uow).ConfigureAwait(false)).Where(row => WorkflowSyncRules.ReportsFolders(row.Kind)))
            {
                if (_connections.ConnectionFromRow(row) is { } connection)
                {
                    syncing.Add((row.Id, connection));
                }
            }

            return syncing;
        }
    }

    private async Task SyncConnectionAsync(long connectionId, ManagerConnection connection, string trigger, CancellationToken cancellationToken)
    {
        if (_connections.Ports.PortForKind(connection.Kind) is not { } port)
        {
            return;
        }

        var description = await port.DescribeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (description.Status != SignalStatus.Reported)
        {
            LogNotAnswering(_logger, connection.Label);
            return;
        }

        var answer = await DelunoDestinationsReader.ReadAsync(connection, null, _handlers, _logger, cancellationToken).ConfigureAwait(false);
        var wanted = WorkflowSyncRules.LibrariesOf(connection.Label, description.Libraries, answer);
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var applied = await new Run(this, uow, connection, connectionId, trigger).ApplyAsync(wanted, description.Libraries).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
            if (applied)
            {
                _scanChanges.Record();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping.Cancel();
            _wake?.Cancel();
        }

        _running.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Weir could not check which media managers still have no workflows, so it will not look again until the next scheduled update.")]
    private static partial void LogLookFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Label} has not reported any libraries for Weir's workflows yet, so Weir will look again in {Seconds} seconds.")]
    private static partial void LogWillLookAgain(ILogger logger, string label, int seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Label} did not answer, so Weir left its workflows as they are and will look again.")]
    private static partial void LogNotAnswering(ILogger logger, string label);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Weir could not set up its workflows from a media manager; it will try again.")]
    private static partial void LogSyncFailed(ILogger logger, Exception error);
}
