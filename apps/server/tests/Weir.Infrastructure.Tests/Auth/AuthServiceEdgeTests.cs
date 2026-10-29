using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Auth;

/// <summary>What the account and session rules do at their edges: a missing or switched-off account, odd input, a session that is no longer valid.</summary>
public sealed class AuthServiceEdgeTests
{
    private const string Password = "test-password-strong";

    private static async Task<UserRecord> SeedAsync(StoreFixture fixture, string name = "alice", bool active = true)
    {
        var hash = CheapPasswordHash.For(Password);
        var id = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, name, hash, "admin", active));
        return new UserRecord(id, name, hash, "admin", active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_account_that_is_missing_or_switched_off_cannot_be_changed(bool exists)
    {
        using var fixture = new StoreFixture();
        var userId = exists ? (await SeedAsync(fixture, active: false)).Id : 9999;

        var username = await Assert.ThrowsAsync<WireValueException>(() =>
            fixture.WithUnitOfWork(uow => fixture.Auth.ChangeUsernameAsync(uow, userId, Password, "bob")));
        var password = await Assert.ThrowsAsync<WireValueException>(() =>
            fixture.WithUnitOfWork(async uow =>
            {
                await fixture.Auth.ChangePasswordAsync(uow, userId, Password, "another-strong-password");
                return 0;
            }));
        var theme = await Assert.ThrowsAsync<WireValueException>(() =>
            fixture.WithUnitOfWork(async uow =>
            {
                await fixture.Auth.SetAppThemeAsync(uow, userId, "dark");
                return 0;
            }));

        Assert.All([username.Message, password.Message, theme.Message], message => Assert.Equal("Account is not available.", message));
    }

    [Fact]
    public async Task A_username_is_required_and_may_change_in_letter_case_alone()
    {
        using var fixture = new StoreFixture();
        var user = await SeedAsync(fixture);

        var required = await Assert.ThrowsAsync<WireValueException>(() =>
            fixture.WithUnitOfWork(uow => fixture.Auth.ChangeUsernameAsync(uow, user.Id, Password, null!)));
        var renamed = await fixture.WithUnitOfWork(uow => fixture.Auth.ChangeUsernameAsync(uow, user.Id, Password, "Alice"));

        Assert.Equal("Username is required.", required.Message);
        Assert.Equal("Alice", renamed);
    }

    [Fact]
    public async Task A_strong_new_password_is_stored_and_ends_every_session_while_a_weak_one_is_refused()
    {
        using var fixture = new StoreFixture();
        var user = await SeedAsync(fixture);
        await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "Browser"));

        var weak = await Assert.ThrowsAsync<WireValueException>(() =>
            fixture.WithUnitOfWork(async uow =>
            {
                await fixture.Auth.ChangePasswordAsync(uow, user.Id, Password, "short");
                return 0;
            }));
        await fixture.WithUnitOfWork(async uow =>
        {
            await fixture.Auth.ChangePasswordAsync(uow, user.Id, Password, "a-different-strong-password");
            return 0;
        });

        Assert.False(string.IsNullOrEmpty(weak.Message));
        Assert.Equal(0, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE revoked_at IS NULL"));
        Assert.NotNull(await fixture.WithUnitOfWork(uow => fixture.Auth.AuthenticateAsync(uow, "alice", "a-different-strong-password")));
    }

    [Fact]
    public async Task The_first_admin_cannot_be_made_while_an_active_admin_exists()
    {
        using var fixture = new StoreFixture();
        await SeedAsync(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.WithUnitOfWork(uow => fixture.Auth.CreateInitialAdminAsync(uow, "bob", "a-strong-password-here")));
    }

    [Fact]
    public async Task A_sign_in_with_no_username_or_with_a_stored_hash_nobody_can_read_fails()
    {
        using var fixture = new StoreFixture();
        var corrupt = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "carol", "not-a-hash", "admin", true));

        Assert.Null(await fixture.WithUnitOfWork(uow => fixture.Auth.AuthenticateAsync(uow, null!, Password)));
        Assert.Null(await fixture.WithUnitOfWork(uow => fixture.Auth.AuthenticateAsync(uow, "carol", Password)));
        Assert.True(corrupt > 0);
    }

    [Fact]
    public async Task A_session_made_with_no_label_gets_the_default_one()
    {
        using var fixture = new StoreFixture();
        var user = await SeedAsync(fixture);

        var (row, _) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, true, null!));

        Assert.Equal(SessionRules.DefaultClientLabel, row.ClientLabel);
        Assert.Equal(SessionRules.DefaultClientLabel, fixture.Auth.SessionPublic(row with { ClientLabel = string.Empty }, current: false).Get("client_label") is WireString label ? label.Value : null);
    }

    [Fact]
    public async Task Cleanup_with_no_moment_given_uses_the_clock()
    {
        using var fixture = new StoreFixture();
        var user = await SeedAsync(fixture);
        var (row, _) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "Browser"));
        await fixture.Execute($"UPDATE user_sessions SET revoked_at = '2026-01-15 10:00:00.000000' WHERE id = '{row.Id}'");

        Assert.Equal(1, await fixture.WithUnitOfWork(uow => fixture.Auth.CleanupInactiveSessionsAsync(uow)));
    }

    [Fact]
    public async Task A_session_is_rejected_when_it_was_revoked_or_timed_out_or_its_account_was_switched_off()
    {
        using var fixture = new StoreFixture(("WEIR_SESSION_IDLE_MINUTES", "30"));
        var user = await SeedAsync(fixture);
        var (revoked, revokedToken) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "a"));
        var (_, idleToken) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "b"));
        var (_, switchedOffToken) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "c"));
        await fixture.Execute($"UPDATE user_sessions SET revoked_at = '2026-01-15 10:00:00.000000' WHERE id = '{revoked.Id}'");

        Assert.Null(await fixture.WithUnitOfWork(uow => fixture.Auth.LoadValidSessionAsync(uow, revokedToken)));
        await fixture.Execute("UPDATE users SET is_active = 0");
        Assert.Null(await fixture.WithUnitOfWork(uow => fixture.Auth.LoadValidSessionAsync(uow, switchedOffToken)));
        await fixture.Execute("UPDATE users SET is_active = 1");
        fixture.Clock.Set(fixture.Clock.GetUtcNow().AddMinutes(31));
        Assert.Null(await fixture.WithUnitOfWork(uow => fixture.Auth.LoadValidSessionAsync(uow, idleToken)));
        Assert.True(await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE revoked_at IS NOT NULL") >= 2);
    }

    [Fact]
    public async Task A_session_whose_account_is_gone_is_rejected()
    {
        using var fixture = new StoreFixture();
        var user = await SeedAsync(fixture);
        var (_, token) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "Browser"));
        await fixture.Execute("PRAGMA foreign_keys = OFF; DELETE FROM users");

        Assert.Null(await fixture.WithUnitOfWork(uow => fixture.Auth.LoadValidSessionAsync(uow, token)));
    }

    [Fact]
    public async Task A_session_seen_long_ago_is_touched_inside_the_transaction_the_request_already_holds()
    {
        using var fixture = new StoreFixture(("WEIR_SESSION_IDLE_MINUTES", "720"));
        var user = await SeedAsync(fixture);
        var (_, token) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "a"));
        fixture.Clock.Set(fixture.Clock.GetUtcNow().AddMinutes(5));

        var held = await fixture.WithUnitOfWork(async uow =>
        {
            await uow.BeginImmediateAsync();
            var session = await fixture.Auth.LoadValidSessionAsync(uow, token);
            return (session, uow.InTransaction);
        });

        Assert.NotNull(held.session);
        Assert.True(held.InTransaction);
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE last_seen_at = '2026-01-15 10:05:00.000000'"));
    }

    [Fact]
    public async Task The_session_list_leaves_out_a_current_session_that_is_no_longer_valid_and_marks_the_one_that_is()
    {
        using var fixture = new StoreFixture();
        var user = await SeedAsync(fixture);
        var (first, _) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "a"));
        fixture.Clock.Set(fixture.Clock.GetUtcNow().AddSeconds(1));
        var (second, _) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "b"));

        var withoutCurrent = await fixture.WithUnitOfWork(uow => fixture.Auth.ListActiveSessionsAsync(uow, user.Id, null));
        var markedCurrent = await fixture.WithUnitOfWork(uow => fixture.Auth.ListActiveSessionsAsync(uow, user.Id, second));
        var staleCurrent = await fixture.WithUnitOfWork(uow =>
            fixture.Auth.ListActiveSessionsAsync(uow, user.Id, first with { RevokedAt = fixture.Auth.Now() }));

        Assert.Equal(2, withoutCurrent.Count);
        Assert.Equal(2, markedCurrent.Count);
        Assert.Single(staleCurrent);
    }

    [Fact]
    public async Task Open_security_pages_hear_of_a_session_change_only_when_a_session_changed()
    {
        using var fixture = new StoreFixture();
        var changes = new DataChangePublisher();
        var users = new AuthStore(changes);
        var user = await SeedAsync(fixture);
        using var stream = changes.Subscribe();

        Assert.Equal(0, await fixture.WithUnitOfWork(uow => users.RevokeSessionAsync(uow, "no-such-session", fixture.Auth.Now())));
        Assert.Equal(0, await fixture.WithUnitOfWork(uow => users.RevokeActiveSessionsForUserAsync(uow, user.Id, fixture.Auth.Now())));
        using var auth = new AuthService(fixture.Options, fixture.Clock, fixture.Database, users, new RecoveryCodeStore(), NullLoggerFactory.Instance);
        await fixture.WithUnitOfWork(uow => auth.CreateSessionAsync(uow, user, false, "Browser"));

        var heard = stream.ReadAsync(CancellationToken.None);
        Assert.True(heard.IsCompleted);
        Assert.Equal(DataTopics.Sessions, await heard);
        Assert.Equal(1, await fixture.WithUnitOfWork(uow => users.RevokeActiveSessionsForUserAsync(uow, user.Id, fixture.Auth.Now())));
    }

    [Fact]
    public async Task An_account_is_switched_on_and_off_by_its_id()
    {
        using var fixture = new StoreFixture();
        var user = await SeedAsync(fixture, active: false);

        await fixture.WithUnitOfWork(uow => fixture.Users.SetActiveAsync(uow, user.Id, true));
        Assert.Equal(1, await fixture.Scalar("SELECT is_active FROM users"));
        await fixture.WithUnitOfWork(uow => fixture.Users.SetActiveAsync(uow, user.Id, false));
        Assert.Equal(0, await fixture.Scalar("SELECT is_active FROM users"));
    }

    [Fact]
    public async Task One_session_is_revoked_once_and_the_others_can_all_be_revoked_when_none_is_current()
    {
        using var fixture = new StoreFixture();
        var user = await SeedAsync(fixture);
        var (first, _) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "a"));
        await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "b"));
        var firstId = Guid.ParseExact(first.Id, "N");

        Assert.False(await fixture.WithUnitOfWork(uow => fixture.Auth.RevokeUserSessionAsync(uow, user.Id, Guid.NewGuid())));
        Assert.True(await fixture.WithUnitOfWork(uow => fixture.Auth.RevokeUserSessionAsync(uow, user.Id, firstId)));
        Assert.False(await fixture.WithUnitOfWork(uow => fixture.Auth.RevokeUserSessionAsync(uow, user.Id, firstId)));
        Assert.Equal(1, await fixture.WithUnitOfWork(uow => fixture.Auth.RevokeOtherUserSessionsAsync(uow, user.Id, null)));
    }
}
