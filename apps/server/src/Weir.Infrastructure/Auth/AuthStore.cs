using Microsoft.Data.Sqlite;
using Weir.Core.Auth;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Auth;

/// <summary>The <c>users</c> and <c>user_sessions</c> tables and the queries sign-in, sessions and recovery run on them.</summary>
public sealed class AuthStore
{
    private const string UserColumns = "id, username, password_hash, role, is_active, app_theme";

    private const string SessionColumns =
        "id, user_id, token_hash, created_at, absolute_expires_at, is_trusted_device, last_seen_at, revoked_at, client_label";

    private readonly DataChangePublisher? _changes;

    /// <param name="changes">Told, once a write commits, that the signed-in browsers changed, so every open Security page reads them again.</param>
    public AuthStore(DataChangePublisher? changes = null)
    {
        _changes = changes;
    }

    public Task<long> CountActiveAdminsAsync(UnitOfWork uow) =>
        Checked(uow).CountAsync("SELECT count(*) FROM users WHERE users.role = $role AND users.is_active IS 1", ("$role", UserRoles.Admin));

    public Task<long> CountUsersAsync(UnitOfWork uow) => Checked(uow).CountAsync("SELECT count(*) FROM users");

    public Task<UserRecord?> GetUserAsync(UnitOfWork uow, long id) =>
        Checked(uow).QuerySingleAsync($"SELECT {UserColumns} FROM users WHERE users.id = $id", ReadUser, ("$id", id));

    /// <summary>The account Weir signs in as: its first active admin, which is the only account there is.</summary>
    public Task<UserRecord?> FindActiveAdminAsync(UnitOfWork uow) =>
        Checked(uow).QuerySingleAsync(
            $"SELECT {UserColumns} FROM users WHERE users.role = $role AND users.is_active IS 1 ORDER BY users.id LIMIT 1 OFFSET 0",
            ReadUser,
            ("$role", UserRoles.Admin));

    /// <summary>The first user whose lower-cased username equals <paramref name="folded"/>.</summary>
    public Task<UserRecord?> FindUserByLowerUsernameAsync(UnitOfWork uow, string folded) =>
        Checked(uow).QuerySingleAsync(
            $"SELECT {UserColumns} FROM users WHERE lower(users.username) = $folded LIMIT 1 OFFSET 0",
            ReadUser,
            ("$folded", folded));

    public Task<int> DeleteAdminsAsync(UnitOfWork uow) =>
        Checked(uow).ExecuteAsync("DELETE FROM users WHERE users.role = $role", ("$role", UserRoles.Admin));

    /// <summary>Insert a user; <see cref="SqliteException"/> with a constraint code on a username clash.</summary>
    public async Task<long> InsertUserAsync(UnitOfWork uow, string username, string passwordHash, string role, bool isActive)
    {
        var id = await Checked(uow).ExecuteScalarWriteAsync(
            "INSERT INTO users (username, password_hash, role, is_active) VALUES ($username, $hash, $role, $active) RETURNING id",
            ("$username", username),
            ("$hash", passwordHash),
            ("$role", role),
            ("$active", isActive ? 1 : 0)).ConfigureAwait(false);
        return Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<int> UpdatePasswordHashAsync(UnitOfWork uow, long userId, string passwordHash) =>
        Checked(uow).ExecuteAsync(
            "UPDATE users SET password_hash=$hash, updated_at=CURRENT_TIMESTAMP WHERE users.id = $id",
            ("$hash", passwordHash),
            ("$id", userId));

    public Task<int> UpdateUsernameAsync(UnitOfWork uow, long userId, string username) =>
        Checked(uow).ExecuteAsync(
            "UPDATE users SET username=$username, updated_at=CURRENT_TIMESTAMP WHERE users.id = $id",
            ("$username", username),
            ("$id", userId));

    public Task<int> SetActiveAsync(UnitOfWork uow, long userId, bool isActive) =>
        Checked(uow).ExecuteAsync(
            "UPDATE users SET is_active=$active, updated_at=CURRENT_TIMESTAMP WHERE users.id = $id",
            ("$active", isActive ? 1 : 0),
            ("$id", userId));

    public Task<int> UpdateAppThemeAsync(UnitOfWork uow, long userId, string appTheme) =>
        Checked(uow).ExecuteAsync(
            "UPDATE users SET app_theme=$theme, updated_at=CURRENT_TIMESTAMP WHERE users.id = $id",
            ("$theme", appTheme),
            ("$id", userId));

    /// <summary>Every account, alphabetically.</summary>
    public Task<List<UserRecord>> ListUsersByUsernameAsync(UnitOfWork uow) =>
        Checked(uow).QueryAsync($"SELECT {UserColumns} FROM users ORDER BY users.username", ReadUser);

    /// <summary>
    /// The account to recover: the named account (case-insensitive), or — when no username is given — the
    /// very first account by id (only meaningful when exactly one exists, the caller's job to check).
    /// </summary>
    public Task<UserRecord?> FindAccountForRecoveryAsync(UnitOfWork uow, string? username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return Checked(uow).QuerySingleAsync($"SELECT {UserColumns} FROM users ORDER BY users.id LIMIT 1 OFFSET 0", ReadUser);
        }

        var folded = username.Trim().ToLowerInvariant();
        return Checked(uow).QuerySingleAsync(
            $"SELECT {UserColumns} FROM users WHERE lower(users.username) = $folded ORDER BY users.id LIMIT 1 OFFSET 0",
            ReadUser,
            ("$folded", folded));
    }

    public Task<UserSessionRecord?> FindSessionByTokenHashAsync(UnitOfWork uow, string tokenHash) =>
        Checked(uow).QuerySingleAsync(
            $"SELECT {SessionColumns} FROM user_sessions WHERE user_sessions.token_hash = $hash LIMIT 1 OFFSET 0",
            ReadSession,
            ("$hash", tokenHash));

    public Task<UserSessionRecord?> FindUserSessionAsync(UnitOfWork uow, long userId, string sessionHexId) =>
        Checked(uow).QuerySingleAsync(
            $"SELECT {SessionColumns} FROM user_sessions WHERE user_sessions.user_id = $user AND user_sessions.id = $id",
            ReadSession,
            ("$user", userId),
            ("$id", sessionHexId));

    public async Task InsertSessionAsync(UnitOfWork uow, UserSessionRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        await Checked(uow).ExecuteAsync(
            "INSERT INTO user_sessions (id, user_id, token_hash, created_at, absolute_expires_at, is_trusted_device, last_seen_at, revoked_at, client_label) " +
            "VALUES ($id, $user, $hash, $created, $expires, $trusted, $seen, $revoked, $label)",
            ("$id", row.Id),
            ("$user", row.UserId),
            ("$hash", row.TokenHash),
            ("$created", row.CreatedAt.ToSqlite()),
            ("$expires", row.AbsoluteExpiresAt.ToSqlite()),
            ("$trusted", row.IsTrustedDevice ? 1 : 0),
            ("$seen", row.LastSeenAt.ToSqlite()),
            ("$revoked", SqliteValues.ToSqlite(row.RevokedAt)),
            ("$label", row.ClientLabel)).ConfigureAwait(false);
        SessionsChanged(uow);
    }

    public async Task<int> RevokeSessionAsync(UnitOfWork uow, string sessionHexId, Timestamp at)
    {
        var revoked = await Checked(uow).ExecuteAsync(
            "UPDATE user_sessions SET revoked_at=$at WHERE user_sessions.id = $id",
            ("$at", at.ToSqlite()),
            ("$id", sessionHexId)).ConfigureAwait(false);
        if (revoked > 0)
        {
            SessionsChanged(uow);
        }

        return revoked;
    }

    public Task<int> TouchSessionAsync(UnitOfWork uow, string sessionHexId, Timestamp at) =>
        Checked(uow).ExecuteAsync(
            "UPDATE user_sessions SET last_seen_at=$at WHERE user_sessions.id = $id",
            ("$at", at.ToSqlite()),
            ("$id", sessionHexId));

    /// <summary>Revokes every unrevoked session of the user.</summary>
    public async Task<int> RevokeActiveSessionsForUserAsync(UnitOfWork uow, long userId, Timestamp at)
    {
        var revoked = await Checked(uow).ExecuteAsync(
            "UPDATE user_sessions SET revoked_at=$at WHERE user_sessions.user_id = $user AND user_sessions.revoked_at IS NULL",
            ("$at", at.ToSqlite()),
            ("$user", userId)).ConfigureAwait(false);
        if (revoked > 0)
        {
            SessionsChanged(uow);
        }

        return revoked;
    }

    public Task<long> CountActiveSessionsAsync(UnitOfWork uow, long userId, Timestamp now) =>
        Checked(uow).CountAsync(
            "SELECT count(*) FROM user_sessions WHERE user_sessions.user_id = $user AND user_sessions.revoked_at IS NULL AND user_sessions.absolute_expires_at > $now",
            ("$user", userId),
            ("$now", now.ToSqlite()));

    public Task<List<UserSessionRecord>> OldestActiveSessionsAsync(UnitOfWork uow, long userId, Timestamp now, long limit) =>
        Checked(uow).QueryAsync(
            $"SELECT {SessionColumns} FROM user_sessions WHERE user_sessions.user_id = $user AND user_sessions.revoked_at IS NULL " +
            "AND user_sessions.absolute_expires_at > $now ORDER BY user_sessions.created_at ASC, user_sessions.id ASC LIMIT $limit OFFSET 0",
            ReadSession,
            ("$user", userId),
            ("$now", now.ToSqlite()),
            ("$limit", limit));

    /// <summary>Unrevoked, unexpired sessions for the session list, newest activity first.</summary>
    public Task<List<UserSessionRecord>> ActiveSessionsNewestFirstAsync(UnitOfWork uow, long userId, Timestamp now) =>
        Checked(uow).QueryAsync(
            $"SELECT {SessionColumns} FROM user_sessions WHERE user_sessions.user_id = $user AND user_sessions.revoked_at IS NULL " +
            "AND user_sessions.absolute_expires_at > $now ORDER BY user_sessions.last_seen_at DESC, user_sessions.created_at DESC",
            ReadSession,
            ("$user", userId),
            ("$now", now.ToSqlite()));

    public Task<List<UserSessionRecord>> ActiveSessionsAsync(UnitOfWork uow, long userId, Timestamp now) =>
        Checked(uow).QueryAsync(
            $"SELECT {SessionColumns} FROM user_sessions WHERE user_sessions.user_id = $user AND user_sessions.revoked_at IS NULL " +
            "AND user_sessions.absolute_expires_at > $now",
            ReadSession,
            ("$user", userId),
            ("$now", now.ToSqlite()));

    /// <summary>Deletes sessions that are revoked, past their absolute expiry, or idle past their cutoff.</summary>
    public Task<int> DeleteInactiveSessionsAsync(UnitOfWork uow, Timestamp now, Timestamp idleCutoff, Timestamp trustedIdleCutoff) =>
        Checked(uow).ExecuteAsync(
            "DELETE FROM user_sessions WHERE user_sessions.revoked_at IS NOT NULL OR user_sessions.absolute_expires_at <= $now " +
            "OR user_sessions.is_trusted_device IS 0 AND user_sessions.last_seen_at < $idle " +
            "OR user_sessions.is_trusted_device IS 1 AND user_sessions.last_seen_at < $trusted",
            ("$now", now.ToSqlite()),
            ("$idle", idleCutoff.ToSqlite()),
            ("$trusted", trustedIdleCutoff.ToSqlite()));

    private void SessionsChanged(UnitOfWork uow) => _changes?.PublishOnCommit(uow, DataTopics.Sessions);

    private static UnitOfWork Checked(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow;
    }

    private static UserRecord ReadUser(SqliteDataReader reader) => new(
        SqliteValues.GetInt64(reader, 0),
        SqliteValues.GetString(reader, 1),
        SqliteValues.GetString(reader, 2),
        SqliteValues.GetString(reader, 3),
        SqliteValues.GetBool(reader, 4),
        SqliteValues.GetStringOrNull(reader, 5));

    private static UserSessionRecord ReadSession(SqliteDataReader reader) => new(
        SqliteValues.GetString(reader, 0),
        SqliteValues.GetInt64(reader, 1),
        SqliteValues.GetString(reader, 2),
        SqliteValues.GetDateTime(reader, 3),
        SqliteValues.GetDateTime(reader, 4),
        SqliteValues.GetBool(reader, 5),
        SqliteValues.GetDateTime(reader, 6),
        SqliteValues.GetDateTimeOrNull(reader, 7),
        SqliteValues.GetString(reader, 8));
}
