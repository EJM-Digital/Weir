using Microsoft.Extensions.Logging;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Core.Time;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Auth;

/// <summary>Password verification, login, and session creation/limiting/cleanup.</summary>
public sealed partial class AuthService
{
    /// <summary>Checks a username and password, padding a missing or inactive account with a dummy verify so timing does not reveal which accounts exist.</summary>
    public async Task<UserRecord?> AuthenticateAsync(UnitOfWork uow, string username, string password)
    {
        var user = await _users.FindUserByLowerUsernameAsync(uow, (username ?? string.Empty).Trim().ToLowerInvariant()).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            await VerifyPasswordAsync(password, PasswordHasher.DummyPasswordHash).ConfigureAwait(false);
            return null;
        }

        return await VerifyPasswordAsync(password, user.PasswordHash).ConfigureAwait(false) ? user : null;
    }

    /// <summary>
    /// Verifies a password against a stored hash, logging a hash that cannot be read, inside
    /// <see cref="_argon2Concurrency"/>.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(string plain, string passwordHash)
    {
        await _argon2Concurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = PasswordHasher.Verify(plain, passwordHash);
            if (result == PasswordVerification.InvalidHash)
            {
                _logger.LogWarning("password verify: stored hash is invalid or unsupported (user record may be corrupt)");
            }

            return result == PasswordVerification.Match;
        }
        finally
        {
            _argon2Concurrency.Release();
        }
    }

    /// <summary>Signs a user in: checks the credentials and creates a session.</summary>
    public async Task<(UserRecord User, UserSessionRecord Session, string RawToken)?> LoginAsync(
        UnitOfWork uow, string username, string password, bool trustedDevice, string clientLabel)
    {
        var user = await AuthenticateAsync(uow, username, password).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var (row, raw) = await CreateSessionAsync(uow, user, trustedDevice, clientLabel).ConfigureAwait(false);
        return (user, row, raw);
    }

    /// <summary>Creates a session and its raw token, then enforces the per-user session cap.</summary>
    public async Task<(UserSessionRecord Row, string RawToken)> CreateSessionAsync(UnitOfWork uow, UserRecord user, bool trustedDevice, string clientLabel)
    {
        ArgumentNullException.ThrowIfNull(user);
        var raw = SessionTokens.Generate();
        var now = Now();
        var ttl = SessionRules.Days(SessionRules.AbsoluteTimeoutDays(trustedDevice, _options));
        var label = (clientLabel ?? string.Empty).Trim();
        var row = new UserSessionRecord(
            Guid.NewGuid().ToString("N"),
            user.Id,
            SessionTokens.Hash(raw),
            now,
            Timestamp.FromUtc(SessionRules.SafeAdd(now.AsUtc, ttl)),
            trustedDevice,
            now,
            null,
            WireStrings.Slice(label.Length == 0 ? SessionRules.DefaultClientLabel : label, 80));
        await _users.InsertSessionAsync(uow, row).ConfigureAwait(false);
        var revoked = await EnforceSessionLimitAsync(uow, user.Id).ConfigureAwait(false);
        if (revoked > 0)
        {
            _logger.LogInformation("auth event: oldest sessions revoked after session cap (user_id={UserId}, count={Count})", user.Id, revoked);
        }

        _logger.LogInformation("auth event: session created");
        return (row, raw);
    }

    /// <summary>Keeps the newest five active sessions and revokes the rest.</summary>
    public async Task<int> EnforceSessionLimitAsync(UnitOfWork uow, long userId, int maxActiveSessions = SessionRules.MaxActiveSessionsPerUser)
    {
        var cap = Math.Max(1, maxActiveSessions);
        var now = Now();
        var active = await _users.CountActiveSessionsAsync(uow, userId, now).ConfigureAwait(false);
        var overflow = active - cap;
        if (overflow <= 0)
        {
            return 0;
        }

        var rows = await _users.OldestActiveSessionsAsync(uow, userId, now, overflow).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await _users.RevokeSessionAsync(uow, row.Id, now).ConfigureAwait(false);
        }

        return rows.Count;
    }

    /// <summary>Revokes the session the cookie names, if it is still valid.</summary>
    public async Task<bool> LogoutByCookieAsync(UnitOfWork uow, string? rawCookieToken)
    {
        var pair = await LoadValidSessionAsync(uow, rawCookieToken).ConfigureAwait(false);
        if (pair is null)
        {
            return false;
        }

        await _users.RevokeSessionAsync(uow, pair.Session.Id, Now()).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes sessions that are revoked, expired or idle past their timeout.</summary>
    public Task<int> CleanupInactiveSessionsAsync(UnitOfWork uow)
    {
        var moment = Now();
        var idleCutoff = Timestamp.FromUtc(SessionRules.SafeSubtract(moment.AsUtc, SessionRules.Minutes(_options.SessionIdleMinutes)));
        var trustedCutoff = Timestamp.FromUtc(SessionRules.SafeSubtract(moment.AsUtc, SessionRules.Minutes(_options.SessionTrustedIdleMinutes)));
        return _users.DeleteInactiveSessionsAsync(uow, moment, idleCutoff, trustedCutoff);
    }
}
