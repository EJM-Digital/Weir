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
    private bool _draining;
    private bool _again;

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
    /// after it, so a burst of saves never queues a sync each.
    /// </summary>
    public void RequestSync()
    {
        lock (_gate)
        {
            if (_draining)
            {
                _again = true;
                return;
            }

            _draining = true;
        }

        _ = Task.Run(DrainAsync, CancellationToken.None);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            try
            {
                await SyncAllAsync(ManualTrigger, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A sync nobody is waiting for must never end the process; the next request or run tries again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSyncFailed(_logger, exception);
            }

            lock (_gate)
            {
                if (!_again)
                {
                    _draining = false;
                    return;
                }

                _again = false;
            }
        }
    }

    /// <summary>One pass over every enabled Deluno connection. <paramref name="trigger"/> is why it runs, as an Activity trigger.</summary>
    public async Task SyncAllAsync(string trigger, CancellationToken cancellationToken)
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
                await SyncConnectionAsync(connectionId, connection, trigger, cancellationToken).ConfigureAwait(false);
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

    public void Dispose() => _running.Dispose();

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Label} did not answer, so Weir left its workflows as they are and will look again.")]
    private static partial void LogNotAnswering(ILogger logger, string label);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Weir could not set up its workflows from a media manager; it will try again.")]
    private static partial void LogSyncFailed(ILogger logger, Exception error);
}
