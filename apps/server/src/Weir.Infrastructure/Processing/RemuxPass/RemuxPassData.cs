using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>Short SQLite writes that retry briefly when another local writer holds the lock.</summary>
public static class LockedWrites
{
    private const int Attempts = 4;

    /// <summary>Run <paramref name="work"/> in its own unit of work and commit, retrying a locked database.</summary>
    public static async Task<T> RunAsync<T>(SqliteDatabase database, Func<UnitOfWork, Task<T>> work, ILogger logger, string label, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(logger);
        var delay = TimeSpan.FromSeconds(0.1);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var uow = await UnitOfWork.OpenAsync(database, cancellationToken).ConfigureAwait(false);
                await using (uow.ConfigureAwait(false))
                {
                    var result = await work(uow).ConfigureAwait(false);
                    await uow.CommitAsync().ConfigureAwait(false);
                    return result;
                }
            }
            catch (SqliteException exception) when (IsLock(exception) && attempt < Attempts - 1)
            {
                logger.LogWarning("Metadata write is waiting for SQLite ({Label}; retry {Attempt}/{Max}).", label, attempt + 1, Attempts - 1);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    public static Task RunAsync(SqliteDatabase database, Func<UnitOfWork, Task> work, ILogger logger, string label, CancellationToken cancellationToken = default) =>
        RunAsync<bool>(
            database,
            async uow =>
            {
                await work(uow).ConfigureAwait(false);
                return true;
            },
            logger,
            label,
            cancellationToken);

    /// <summary><c>database is locked</c> or <c>database table is locked</c>.</summary>
    public static bool IsLock(SqliteException exception) =>
        exception is not null && DatabaseBusyException.IsBusy(exception);
}

/// <summary>The pass's database and manager reads and writes over SQLite and the media manager ports.</summary>
public sealed class SqliteRemuxPassData : IRemuxPassFileFacts, IPostSuccessCleanupData
{
    private readonly SqliteDatabase _database;
    private readonly MediaManagerConnectionService _connections;
    private readonly ILogger<SqliteRemuxPassData> _logger;

    public SqliteRemuxPassData(SqliteDatabase database, MediaManagerConnectionService connections, ILogger<SqliteRemuxPassData> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task RecordMeasuredMediaFactsAsync(MeasuredMediaFacts facts, CancellationToken cancellationToken) =>
        BestEffortAsync(
            async uow =>
            {
                await RemuxPassFileState.RecordMeasuredMediaFactsAsync(uow, facts).ConfigureAwait(false);
                if (facts.JobId is { } jobId)
                {
                    RunnerCosts.RecordMeasured(
                        uow.Connection, await uow.WriteTransactionAsync().ConfigureAwait(false), jobId, RunnerUnits.ResolutionClassForDimensions(facts.VideoWidth, facts.VideoHeight));
                }
            },
            "measured media facts",
            cancellationToken);

    public Task RecordOutputCollisionAsync(string relativePath, CollisionDecision decision, long? libraryId, CancellationToken cancellationToken) =>
        BestEffortAsync(uow => RemuxPassFileState.RecordOutputCollisionAsync(uow, relativePath, decision, libraryId), "output collision", cancellationToken);

    public async Task<IReadOnlyList<ManagerLibraryTruth>> CollectLibraryTruthAsync(string mediaScope, CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            return await _connections.CollectLibraryTruthAsync(uow, mediaScope, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ActiveRemuxJob>> ActiveRemuxJobsAsync(CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            return await uow.QueryAsync(
                "SELECT id, payload_json FROM jobs WHERE job_kind = $kind AND status IN ('pending', 'leased') ORDER BY id",
                reader => new ActiveRemuxJob(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)),
                ("$kind", RemuxPassOutcomes.JobKind)).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the ledger already recorded this hand-off's outcome as delivered (#545).</summary>
    public async Task<bool> HandoffOutcomeAcknowledgedAsync(HandoffOrigin? origin, CancellationToken cancellationToken)
    {
        if (origin is not { HandoffId.Length: > 0 })
        {
            return false;
        }

        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var row = await HandoffLedgerStore.FindAsync(uow, origin.SourceKey, origin.HandoffId!).ConfigureAwait(false);
            return row is not null && row.State is HandoffLedgerRules.Completed or HandoffLedgerRules.PassedThrough;
        }
    }

    /// <summary>Commits optional file metadata, logging a failure instead of throwing: optional metadata must never make
    /// a safe file mutation look like a failed remux.</summary>
    private async Task BestEffortAsync(Func<UnitOfWork, Task> work, string label, CancellationToken cancellationToken)
    {
        try
        {
            await LockedWrites.RunAsync(_database, work, _logger, label, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Weir could not commit optional file metadata ({Label}); continuing the media pass.", label);
        }
    }
}

/// <summary>
/// #531 item 2: a retry or requeue of a handed-over file must still carry the hand-off origin, so the final completion,
/// hold or pass-through is reported to the manager with its output path. The origin is found on the most recent job for
/// the same file that carried one, and only while that hand-off has not already been answered.
/// </summary>
public static class HandoffOriginCarry
{
    private static readonly string[] Kinds = [RemuxPassOutcomes.JobKind, IntakeRules.PassThroughJobKind, IntakeRules.RejectJobKind];

    /// <summary>The origin to carry for this file, or null.</summary>
    public static async Task<WireObject?> FindAsync(UnitOfWork uow, long? libraryId, string relativePath, long? excludeJobId = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var rows = await uow.QueryAsync(
            "SELECT id, payload_json FROM jobs WHERE job_kind IN ($a, $b, $c) AND payload_json LIKE '%\"origin\"%' ORDER BY id DESC LIMIT 500",
            reader => (Id: reader.GetInt64(0), Payload: reader.IsDBNull(1) ? null : reader.GetString(1)),
            ("$a", Kinds[0]),
            ("$b", Kinds[1]),
            ("$c", Kinds[2])).ConfigureAwait(false);
        foreach (var (id, payload) in rows)
        {
            if (id == excludeJobId || payload is null)
            {
                continue;
            }

            WireValue parsed;
            try
            {
                parsed = WireJsonParser.Parse(payload);
            }
            catch (WireJsonDecodeException)
            {
                continue;
            }

            if (parsed is not WireObject dict ||
                dict.Get("relative_media_path") is not WireString path || WireStrings.Strip(path.Value) != WireStrings.Strip(relativePath) ||
                dict.Get("origin") is not WireObject origin)
            {
                continue;
            }

            if (libraryId is { } wanted && dict.Get("library_id") is WireInteger jobLibrary && jobLibrary.Value != wanted)
            {
                continue;
            }

            if (HandoffOrigin.FromPayload(dict) is not { HandoffId: { } handoffId } parsedOrigin)
            {
                continue;
            }

            var ledger = await HandoffLedgerStore.FindAsync(uow, parsedOrigin.SourceKey, handoffId).ConfigureAwait(false);
            if (ledger is null || ledger.State is HandoffLedgerRules.Completed or HandoffLedgerRules.PassedThrough or HandoffLedgerRules.Rejected or HandoffLedgerRules.Cancelled)
            {
                return null;
            }

            return origin;
        }

        return null;
    }
}
