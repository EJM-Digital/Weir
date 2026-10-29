using Microsoft.Data.Sqlite;
using Weir.Core.Time;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Auth;

/// <summary>A stored recovery code that has not been used, with the username of the account it belongs to.</summary>
public sealed record RecoveryCodeRecord(long Id, long UserId, string Username, string CodeHash);

/// <summary>How many of an account's recovery codes can still be used, and whether the person said they saved them.</summary>
public sealed record RecoveryCodeStatus(int Remaining, bool Confirmed);

/// <summary>The <c>recovery_codes</c> table: only a hash of each code is kept.</summary>
public sealed class RecoveryCodeStore
{
    /// <summary>Replaces the account's codes with a new set, so every old code stops working.</summary>
    public async Task ReplaceSetAsync(UnitOfWork uow, long userId, IReadOnlyList<(string Lookup, string CodeHash)> codes, Timestamp at)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(codes);
        await uow.ExecuteAsync("DELETE FROM recovery_codes WHERE recovery_codes.user_id = $user", ("$user", userId)).ConfigureAwait(false);
        foreach (var (lookup, codeHash) in codes)
        {
            await uow.ExecuteAsync(
                "INSERT INTO recovery_codes (user_id, lookup, code_hash, created_at) VALUES ($user, $lookup, $hash, $at)",
                ("$user", userId),
                ("$lookup", lookup),
                ("$hash", codeHash),
                ("$at", at.ToSqlite())).ConfigureAwait(false);
        }
    }

    /// <summary>The unused code whose first group is <paramref name="lookup"/>.</summary>
    public Task<RecoveryCodeRecord?> FindUnusedAsync(UnitOfWork uow, string lookup)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QuerySingleAsync(
            "SELECT recovery_codes.id, recovery_codes.user_id, users.username, recovery_codes.code_hash FROM recovery_codes " +
            "JOIN users ON users.id = recovery_codes.user_id WHERE recovery_codes.lookup = $lookup AND recovery_codes.used_at IS NULL",
            ReadRecord,
            ("$lookup", lookup));
    }

    /// <summary>Spends a code. False when it was already spent, so two requests cannot both use it.</summary>
    public async Task<bool> MarkUsedAsync(UnitOfWork uow, long id, Timestamp at)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var changed = await uow.ExecuteAsync(
            "UPDATE recovery_codes SET used_at = $at WHERE recovery_codes.id = $id AND recovery_codes.used_at IS NULL",
            ("$at", at.ToSqlite()),
            ("$id", id)).ConfigureAwait(false);
        return changed == 1;
    }

    /// <summary>Marks the account's unused codes as saved.</summary>
    public Task<int> ConfirmAsync(UnitOfWork uow, long userId, Timestamp at)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync(
            "UPDATE recovery_codes SET confirmed_at = $at WHERE recovery_codes.user_id = $user AND recovery_codes.used_at IS NULL AND recovery_codes.confirmed_at IS NULL",
            ("$at", at.ToSqlite()),
            ("$user", userId));
    }

    public async Task<RecoveryCodeStatus> StatusAsync(UnitOfWork uow, long userId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var status = await uow.QuerySingleAsync(
            "SELECT count(*), coalesce(sum(recovery_codes.confirmed_at IS NULL), 0) FROM recovery_codes " +
            "WHERE recovery_codes.user_id = $user AND recovery_codes.used_at IS NULL",
            reader =>
            {
                var remaining = (int)SqliteValues.GetInt64(reader, 0);
                return new RecoveryCodeStatus(remaining, remaining > 0 && SqliteValues.GetInt64(reader, 1) == 0);
            },
            ("$user", userId)).ConfigureAwait(false);
        return status!;
    }

    private static RecoveryCodeRecord ReadRecord(SqliteDataReader reader) => new(
        SqliteValues.GetInt64(reader, 0),
        SqliteValues.GetInt64(reader, 1),
        SqliteValues.GetString(reader, 2),
        SqliteValues.GetString(reader, 3));
}
