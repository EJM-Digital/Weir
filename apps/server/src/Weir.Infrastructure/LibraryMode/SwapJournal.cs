using Weir.Core.LibraryMode;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>Where a swap had got to when it was last recorded.</summary>
public enum SwapJournalState
{
    /// <summary>Preflight passed; the cleaned copy is being written to the temp file.</summary>
    Writing,

    /// <summary>Validated and unchanged; about to rename the original aside.</summary>
    Committing,

    /// <summary>The cleaned copy has the original's name (the row's <c>committed</c> flag). The backup may still exist.</summary>
    Committed,

    /// <summary>Committed and the backup is gone.</summary>
    Finished,

    /// <summary>Stopped before the commit and undone.</summary>
    RolledBack,

    /// <summary>Left unfinished by a crash and put right by the startup sweep.</summary>
    Recovered,

    /// <summary>
    /// #735: the startup sweep found a kept original already at its destination whose content did not match its backup —
    /// a crash mid-copy a retry cannot resolve on its own. Recorded once, with an Activity event; the sweep does not
    /// retry it again, so both files are left for a person to look at.
    /// </summary>
    KeepConflict,
}

/// <summary>One job's swap record.</summary>
/// <param name="JobId">The job this swap belongs to.</param>
/// <param name="OriginalPath">The library file being replaced.</param>
/// <param name="State">Where the swap had got to when this was recorded.</param>
/// <param name="KeptOriginalPath">
/// #735: where the original will go (or has gone) instead of being deleted, decided and recorded before the commit
/// rename so a crash after that point can always finish the same move rather than losing the setting's intent.
/// Null while the library's "keep the original after clean" setting is off.
/// </param>
public sealed record SwapJournalEntry(long JobId, string OriginalPath, SwapJournalState State, string? KeptOriginalPath = null)
{
    public bool IsUnfinished => State is SwapJournalState.Writing or SwapJournalState.Committing or SwapJournalState.Committed;
}

/// <summary>The durable record of swaps in progress, so the startup sweep can go straight to the files a crash left behind.</summary>
public interface ISwapJournal
{
    Task RecordAsync(SwapJournalEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SwapJournalEntry>> ListUnfinishedAsync(CancellationToken cancellationToken = default);

    /// <summary>The library files whose kept original needs a person (<see cref="SwapJournalState.KeepConflict"/>). Their backups are not to be touched.</summary>
    Task<IReadOnlyList<string>> ListKeepConflictPathsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Records each swap in the <c>library_swaps</c> table, one row per job (upserted by <c>job_id</c>). Migration
/// 0040_library_swaps (#557) copies older records into it from <c>jobs.payload_json</c>'s <c>library_swap</c>
/// object and <c>swap_committed</c> flag.
/// </summary>
public sealed class ProcessingJobSwapJournal : ISwapJournal
{
    private readonly SqliteDatabase _database;

    public ProcessingJobSwapJournal(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task RecordAsync(SwapJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var exists = await uow.CountAsync("SELECT COUNT(*) FROM jobs WHERE id = @id", ("@id", entry.JobId)).ConfigureAwait(false);
            if (exists == 0)
            {
                throw new InvalidOperationException(
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Job {entry.JobId} does not exist, so its swap could not be recorded."));
            }

            await uow.ExecuteAsync(
                """
                INSERT INTO library_swaps (job_id, state, original_path, temp_path, backup_path, committed, kept_original_path, updated_at)
                VALUES (@job_id, @state, @original_path, @temp_path, @backup_path, @committed, @kept_original_path, CURRENT_TIMESTAMP)
                ON CONFLICT(job_id) DO UPDATE SET state = excluded.state, original_path = excluded.original_path,
                    temp_path = excluded.temp_path, backup_path = excluded.backup_path,
                    committed = committed OR excluded.committed,
                    kept_original_path = COALESCE(excluded.kept_original_path, library_swaps.kept_original_path),
                    updated_at = CURRENT_TIMESTAMP
                """,
                ("@job_id", entry.JobId),
                ("@state", StateName(entry.State)),
                ("@original_path", entry.OriginalPath),
                ("@temp_path", SafeSwapRules.TempPath(entry.OriginalPath)),
                ("@backup_path", SafeSwapRules.BackupPath(entry.OriginalPath)),
                ("@committed", entry.State is SwapJournalState.Committed or SwapJournalState.Finished ? 1 : 0),
                ("@kept_original_path", entry.KeptOriginalPath)).ConfigureAwait(false);

            await uow.CommitAsync().ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SwapJournalEntry>> ListUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<SwapJournalEntry>();
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id, original_path, state, kept_original_path FROM library_swaps ORDER BY job_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ParseState(reader.GetString(2)) is not { } parsed)
            {
                continue;
            }

            var keptOriginalPath = reader.IsDBNull(3) ? null : reader.GetString(3);
            var entry = new SwapJournalEntry(reader.GetInt64(0), reader.GetString(1), parsed, keptOriginalPath);
            if (entry.IsUnfinished)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public async Task<IReadOnlyList<string>> ListKeepConflictPathsAsync(CancellationToken cancellationToken = default)
    {
        var paths = new List<string>();
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT original_path FROM library_swaps WHERE state = @state AND original_path IS NOT NULL ORDER BY job_id";
        command.Parameters.AddWithValue("@state", StateName(SwapJournalState.KeepConflict));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    public static string StateName(SwapJournalState state) => state switch
    {
        SwapJournalState.Writing => "writing",
        SwapJournalState.Committing => "committing",
        SwapJournalState.Committed => "committed",
        SwapJournalState.Finished => "finished",
        SwapJournalState.RolledBack => "rolled_back",
        SwapJournalState.Recovered => "recovered",
        SwapJournalState.KeepConflict => "keep_conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static SwapJournalState? ParseState(string name) =>
        Enum.GetValues<SwapJournalState>().Where(state => StateName(state) == name).Select(state => (SwapJournalState?)state).FirstOrDefault();
}
