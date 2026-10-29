using Microsoft.Extensions.Logging;
using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Security;
using Weir.Core.Time;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Auth;

/// <summary>
/// A signed-in request: its session row (as held in memory) and its user. <paramref name="ByHomeNetwork"/> marks a request that
/// skipped sign-in because it came from the home network, whose session row exists only for this request.
/// </summary>
public sealed record SignedInSession(UserSessionRecord Session, UserRecord User, bool ByHomeNetwork = false);

/// <summary>
/// Credentials, first-admin bootstrap, server-side sessions and logout. Split by concern across several files
/// (this one holds construction and the session-validation path that runs on every authenticated request);
/// see <see cref="AuthService"/>'s other partials for credential/session creation, session listing and
/// revocation, and account/bootstrap mutation. One type throughout: callers reach it as <c>request.Auth</c>.
/// </summary>
public sealed partial class AuthService : IDisposable
{
    public const string BootstrapNotAllowedMessage = "bootstrap not allowed: an admin user already exists";

    private readonly WeirOptions _options;
    private readonly TimeProvider _time;
    private readonly SqliteDatabase _database;
    private readonly AuthStore _users;
    private readonly ILogger _logger;

    /// <summary>
    /// Argon2 is deliberately memory-hard (64 MiB per verification), so a burst of concurrent login
    /// attempts is also a burst of memory pressure; capping how many run at once bounds that regardless
    /// of how many requests arrive together.
    /// </summary>
    private readonly SemaphoreSlim _argon2Concurrency = new(Math.Max(1, Environment.ProcessorCount));

    public AuthService(WeirOptions options, TimeProvider time, SqliteDatabase database, AuthStore users, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options;
        _time = time;
        _database = database;
        ArgumentNullException.ThrowIfNull(users);
        _users = users;
        _logger = loggerFactory.CreateLogger("weir.platform.auth.service");
    }

    public void Dispose() => _argon2Concurrency.Dispose();

    public Timestamp Now() => Timestamp.UtcNow(_time);

    /// <summary>The request's session: look up by token hash, enforce revocation and timeouts, and touch <c>last_seen_at</c> at most once a minute.</summary>
    public async Task<SignedInSession?> LoadValidSessionAsync(UnitOfWork uow, string? rawCookieToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (string.IsNullOrEmpty(rawCookieToken))
        {
            return null;
        }

        var row = await _users.FindSessionByTokenHashAsync(uow, SessionTokens.Hash(rawCookieToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var idle = SessionRules.EffectiveIdleTimeout(row.IsTrustedDevice, _options);
        var now = Now();
        var reason = SessionRules.InvalidReason(row, idle, now.AsUtc);
        if (reason is not null)
        {
            if (reason is SessionInvalidReason.AbsoluteExpired or SessionInvalidReason.IdleExpired)
            {
                await RevokeExpiredAsync(uow, row.Id, now).ConfigureAwait(false);
            }

            _logger.LogInformation("auth event: session rejected (reason={Reason}, user_id={UserId})", ReasonText(reason.Value), row.UserId);
            return null;
        }

        var user = await _users.GetUserAsync(uow, row.UserId).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        if (now.AsUtc - row.LastSeenAt.AsUtc >= SessionRules.LastSeenTouchGap(idle))
        {
            // Its own short transaction: on the request's unit of work, a page load's once-a-minute touch would hold the write
            // lock until the whole request finished, and every other lane would wait on a read (#708).
            await WriteOnItsOwnAsync(uow, own => _users.TouchSessionAsync(own, row.Id, now)).ConfigureAwait(false);
            row = row with { LastSeenAt = now };
        }

        return new SignedInSession(row, user);
    }

    /// <summary>
    /// Commits the revoke of an expired session on its own, whatever the request's outcome, so the 401 that
    /// follows cannot roll it back (#529).
    /// </summary>
    private Task RevokeExpiredAsync(UnitOfWork uow, string sessionId, Timestamp now) =>
        WriteOnItsOwnAsync(uow, own => _users.RevokeSessionAsync(own, sessionId, now));

    /// <summary>
    /// Runs <paramref name="write"/> in a unit of work of its own and commits it at once, unless <paramref name="uow"/> is
    /// already inside a transaction: it then holds the write lock, and a second connection would wait on it.
    /// </summary>
    private async Task WriteOnItsOwnAsync(UnitOfWork uow, Func<UnitOfWork, Task> write)
    {
        if (uow.InTransaction)
        {
            await write(uow).ConfigureAwait(false);
            return;
        }

        // CancellationToken.None, deliberately: this write must land whatever the request's outcome (#529),
        // and that includes the request's own cancellation once its response no longer needs the result.
        var own = await UnitOfWork.OpenAsync(_database, CancellationToken.None).ConfigureAwait(false);
        await using (own.ConfigureAwait(false))
        {
            await write(own).ConfigureAwait(false);
            await own.CommitAsync().ConfigureAwait(false);
        }
    }

    private static string ReasonText(SessionInvalidReason reason) => reason switch
    {
        SessionInvalidReason.Revoked => "revoked",
        SessionInvalidReason.AbsoluteExpired => "absolute_expired",
        _ => "idle_expired",
    };
}
