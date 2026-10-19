using System.Text;
using Microsoft.Data.Sqlite;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Platform;

public sealed class AuthAndSettingsStoreTests
{
    private const string Password = "test-password-strong";

    [Fact]
    public async Task Login_keeps_only_the_newest_five_active_sessions()
    {
        using var fixture = new StoreFixture();
        await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", CheapPasswordHash.For(Password), "admin", true));
        for (var i = 0; i < 6; i++)
        {
            fixture.Clock.Set(fixture.Clock.GetUtcNow().AddSeconds(1));
            var login = await fixture.WithUnitOfWork(uow => fixture.Auth.LoginAsync(uow, "ALICE", Password, false, "Browser session"));
            Assert.NotNull(login);
        }

        Assert.Equal(5, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE revoked_at IS NULL"));
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE revoked_at IS NOT NULL"));
    }

    [Fact]
    public async Task The_session_cap_ignores_sessions_past_their_absolute_expiry()
    {
        using var fixture = new StoreFixture();
        var userId = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", "x", "admin", true));
        var user = new UserRecord(userId, "alice", "x", "admin", true);
        for (var i = 0; i < 5; i++)
        {
            await fixture.WithUnitOfWork(async uow =>
            {
                var (row, _) = await fixture.Auth.CreateSessionAsync(uow, user, false, "Browser session");
                await uow.ExecuteAsync("UPDATE user_sessions SET absolute_expires_at = $at WHERE id = $id", ("$at", "2026-01-15 09:59:59.000000"), ("$id", row.Id));
                return row;
            });
        }

        await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "Browser session"));
        Assert.Equal(0, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE revoked_at IS NOT NULL"));
    }

    [Fact]
    public async Task Last_seen_is_persisted_at_most_once_a_minute()
    {
        using var fixture = new StoreFixture(("WEIR_SESSION_IDLE_MINUTES", "720"));
        var userId = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", "x", "admin", true));
        var (_, raw) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, new UserRecord(userId, "alice", "x", "admin", true), false, "b"));

        fixture.Clock.Set(fixture.Clock.GetUtcNow().AddSeconds(30));
        Assert.NotNull(await fixture.WithUnitOfWork(uow => fixture.Auth.LoadValidSessionAsync(uow, raw)));
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE last_seen_at = '2026-01-15 10:00:00.000000'"));

        fixture.Clock.Set(new DateTimeOffset(2026, 1, 15, 10, 1, 1, TimeSpan.Zero));
        var touched = await fixture.WithUnitOfWork(uow => fixture.Auth.LoadValidSessionAsync(uow, raw));
        Assert.True(touched!.Session.LastSeenAt.IsAware);
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE last_seen_at = '2026-01-15 10:01:01.000000'"));
    }

    [Fact]
    public async Task Refreshing_a_session_leaves_the_request_outside_any_transaction()
    {
        using var fixture = new StoreFixture(("WEIR_SESSION_IDLE_MINUTES", "720"));
        var userId = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", "x", "admin", true));
        var (_, raw) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, new UserRecord(userId, "alice", "x", "admin", true), false, "b"));
        fixture.Clock.Set(new DateTimeOffset(2026, 1, 15, 10, 1, 1, TimeSpan.Zero));

        var requestHeldTheLock = await fixture.WithUnitOfWork(
            async uow =>
            {
                await fixture.Auth.LoadValidSessionAsync(uow, raw);
                return uow.InTransaction;
            },
            commit: false);

        Assert.False(requestHeldTheLock);
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE last_seen_at = '2026-01-15 10:01:01.000000'"));
    }

    [Fact]
    public async Task An_expired_session_is_revoked_even_when_the_request_rolls_back()
    {
        // #529: the revoke is committed on its own, so the request's rollback cannot undo it.
        using var fixture = new StoreFixture();
        var userId = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", "x", "admin", true));
        var (_, raw) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, new UserRecord(userId, "alice", "x", "admin", true), false, "b"));

        fixture.Clock.Set(fixture.Clock.GetUtcNow().AddDays(fixture.Options.SessionAbsoluteDays + 1));
        Assert.Null(await fixture.WithUnitOfWork(uow => fixture.Auth.LoadValidSessionAsync(uow, raw), commit: false));
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM user_sessions WHERE revoked_at IS NOT NULL"));
    }

    [Fact]
    public async Task Cleanup_deletes_revoked_and_expired_sessions()
    {
        using var fixture = new StoreFixture(("WEIR_SESSION_IDLE_MINUTES", "720"));
        var userId = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", "x", "admin", true));
        var user = new UserRecord(userId, "alice", "x", "admin", true);
        var (revoked, _) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "b"));
        var (expired, _) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "b"));
        var (active, _) = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateSessionAsync(uow, user, false, "b"));
        await fixture.Execute($"UPDATE user_sessions SET revoked_at = '2026-01-15 10:00:00.000000' WHERE id = '{revoked.Id}'");
        await fixture.Execute($"UPDATE user_sessions SET absolute_expires_at = '2026-01-15 09:59:59.000000' WHERE id = '{expired.Id}'");

        Assert.Equal(2, await fixture.WithUnitOfWork(uow => fixture.Auth.CleanupInactiveSessionsAsync(uow)));
        Assert.Equal(1, await fixture.Scalar($"SELECT count(*) FROM user_sessions WHERE id = '{active.Id}'"));
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM user_sessions"));
    }

    [Fact]
    public async Task An_inactive_sole_admin_reopens_bootstrap_and_recovery_leaves_exactly_one_admin()
    {
        using var fixture = new StoreFixture();
        await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", "argon2-placeholder", "admin", true));
        Assert.False(await fixture.WithUnitOfWork(fixture.Auth.BootstrapAllowedAsync));
        await fixture.Execute("UPDATE users SET is_active = 0");
        Assert.True(await fixture.WithUnitOfWork(fixture.Auth.BootstrapAllowedAsync));

        var created = await fixture.WithUnitOfWork(uow => fixture.Auth.CreateInitialAdminAsync(uow, "alice-again", "recovered-password-strong"));
        Assert.True(created.IsActive);
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM users WHERE role = 'admin'"));
        Assert.False(await fixture.WithUnitOfWork(fixture.Auth.BootstrapAllowedAsync));
    }

    [Fact]
    public async Task Username_and_password_changes_follow_the_documented_rules()
    {
        using var fixture = new StoreFixture();
        var id = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", CheapPasswordHash.For(Password), "admin", true));
        await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "bob", "x", "viewer", true));

        async Task<string> Error(Func<UnitOfWork, Task> work) =>
            (await Assert.ThrowsAsync<WireValueException>(() => fixture.WithUnitOfWork(async uow => { await work(uow); return 0; }))).Message;

        Assert.Equal("Current password is incorrect.", await Error(uow => fixture.Auth.ChangeUsernameAsync(uow, id, "wrong", "user1")));
        Assert.Equal("Username is required.", await Error(uow => fixture.Auth.ChangeUsernameAsync(uow, id, Password, "  ")));
        Assert.Equal("New username must be different from the current username.", await Error(uow => fixture.Auth.ChangeUsernameAsync(uow, id, Password, "alice")));
        Assert.Equal("That username is already taken.", await Error(uow => fixture.Auth.ChangeUsernameAsync(uow, id, Password, "BOB")));
        Assert.Equal("Alice", await fixture.WithUnitOfWork(uow => fixture.Auth.ChangeUsernameAsync(uow, id, Password, "Alice")));
        Assert.Equal("New password must be different from the current password.", await Error(uow => fixture.Auth.ChangePasswordAsync(uow, id, Password, Password)));
        Assert.Equal("Password must use a wider mix of characters.", await Error(uow => fixture.Auth.ChangePasswordAsync(uow, id, Password, "aaaaaaaaaa")));
    }

    [Fact]
    public async Task A_new_user_has_no_theme_preference_until_one_is_set()
    {
        using var fixture = new StoreFixture();
        var id = await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", CheapPasswordHash.For(Password), "admin", true));

        Assert.Null((await fixture.WithUnitOfWork(uow => fixture.Users.GetUserAsync(uow, id)))!.AppTheme);

        await fixture.WithUnitOfWork(async uow =>
        {
            await fixture.Auth.SetAppThemeAsync(uow, id, AppThemes.Dark);
            return 0;
        });

        Assert.Equal(AppThemes.Dark, (await fixture.WithUnitOfWork(uow => fixture.Users.GetUserAsync(uow, id)))!.AppTheme);
    }

    [Fact]
    public async Task A_configuration_snapshot_is_written_when_due_and_path_traversal_is_refused()
    {
        using var fixture = new StoreFixture();
        var zones = new IanaTimeZoneResolver();
        var suiteSettings = new SuiteSettingsStore(fixture.Users);
        var backups = new ConfigurationBackups(fixture.Options, fixture.Clock, zones, suiteSettings, new ConfigurationBundleStore(suiteSettings, BackupParts.Connections()), BackupParts.Passwords());
        await fixture.Execute("UPDATE suite_settings SET configuration_backup_enabled = 1, configuration_backup_interval_hours = 6");

        Assert.Equal(1, await backups.RunTickAsync(fixture.Database, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(0, await backups.RunTickAsync(fixture.Database, new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(1, await fixture.Scalar("SELECT count(*) FROM suite_settings WHERE configuration_backup_last_run_at = '2026-01-15 10:00:00.000000'"));
        var file = Assert.Single(Directory.GetFiles(backups.Directory));
        var text = await File.ReadAllTextAsync(file);
        Assert.StartsWith("{\n  \"arr_library_operator_settings\": {", text, StringComparison.Ordinal);

        await fixture.Execute("INSERT INTO suite_configuration_backup (created_at, file_name, size_bytes) VALUES ('2026-01-01 00:00:00.000000', '../outside.json', 2)");
        var id = await fixture.Scalar("SELECT max(id) FROM suite_configuration_backup");
        var error = await Assert.ThrowsAsync<WireValueException>(() => fixture.WithUnitOfWork(uow => backups.GetFileAsync(uow, id)));
        Assert.Equal("Configuration snapshot file name is invalid.", error.Message);
    }

    [Fact]
    public async Task Only_the_newest_five_snapshots_are_kept()
    {
        using var fixture = new StoreFixture();
        var suiteSettings = new SuiteSettingsStore(fixture.Users);
        var backups = new ConfigurationBackups(
            fixture.Options, fixture.Clock, new IanaTimeZoneResolver(), suiteSettings, new ConfigurationBundleStore(suiteSettings, BackupParts.Connections()), BackupParts.Passwords());
        for (var i = 0; i < 7; i++)
        {
            fixture.Clock.Set(fixture.Clock.GetUtcNow().AddMinutes(1));
            await fixture.WithUnitOfWork(uow => backups.CreateAsync(uow, BackupSource.Manual));
        }

        Assert.Equal(5, await fixture.Scalar("SELECT count(*) FROM suite_configuration_backup"));
        Assert.Equal(5, Directory.GetFiles(backups.Directory).Length);
    }

    [Fact]
    public void Update_settings_files_round_trip_and_fall_back_to_notify_only()
    {
        using var fixture = new StoreFixture();
        var files = new UpdateFiles(fixture.Options);
        Assert.Equal("{\"mode\":\"Auto\",\"check_on_startup\":true,\"check_interval_minutes\":60}", WireJsonWriter.Dumps(files.ReadSettings(), WireJsonFormat.Response));
        var foreign = Path.Join(fixture.Home.Path, "update-settings.json.tmp");
        File.WriteAllText(foreign, "{\"mode\": \"Auto\"}");
        files.WriteSettings("DownloadOnly", false, 240);
        Assert.Equal("{\"mode\":\"DownloadOnly\",\"check_on_startup\":false,\"check_interval_minutes\":240}", WireJsonWriter.Dumps(files.ReadSettings(), WireJsonFormat.Response));
        Assert.True(File.Exists(foreign));
        Assert.Empty(Directory.GetFiles(fixture.Home.Path, ".update-settings.json.*.tmp"));
        File.WriteAllText(Path.Join(fixture.Home.Path, "update-settings.json"), "{\"mode\": \"Notify");
        var warned = new List<string>();
        Assert.Equal("NotifyOnly", ((WireString)files.ReadSettings(warned.Add)["mode"]).Value);
        Assert.Single(warned);

        Assert.Equal("{\"downloaded\":false,\"pending_version\":null,\"state\":\"idle\",\"failure\":null,\"tray_running\":false}", WireJsonWriter.Dumps(files.ReadState(), WireJsonFormat.Response));
        File.WriteAllText(Path.Join(fixture.Home.Path, "update-state.json"), "{\"downloaded\": true, \"version\": \"2.0.0\"}");
        Assert.Equal("{\"downloaded\":true,\"pending_version\":\"2.0.0\",\"state\":\"downloaded\",\"failure\":null,\"tray_running\":false}", WireJsonWriter.Dumps(files.ReadState(), WireJsonFormat.Response));
        files.WriteApplyFlag();
        Assert.True(File.Exists(Path.Join(fixture.Home.Path, "update-apply-now")));
    }

    [Fact]
    public void Log_lines_with_invalid_utf8_are_read_with_replacement_characters()
    {
        // #536: one bad byte must not fail the whole read.
        using var directory = new TempDirectory();
        var path = directory.Join("weir.log");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetBytes("{\"timestamp\":\"2099-01-01T00:00:00Z\",\"level\":\"INFO\",\"logger\":\"weir.x\",\"message\":\"bad "), 0xFF, .. "\"}\n"u8]);
        using var log = new WeirLogFile(path, TimeProvider.System);
        log.WriteLine("{\"timestamp\":\"2099-01-01T00:00:01Z\",\"level\":\"INFO\",\"logger\":\"weir.x\",\"message\":\"good\"}");
        var lines = new List<string>();
        Assert.True(log.ReadLines(lines.Add));
        Assert.Equal(2, lines.Count);
        Assert.Contains("bad �", lines[0], StringComparison.Ordinal);
        Assert.True(log.Prune(3650));
    }

    [Fact]
    public async Task The_log_file_appends_after_content_written_by_someone_else()
    {
        using var directory = new TempDirectory();
        var path = directory.Join("weir.log");
        using var log = new WeirLogFile(path, TimeProvider.System);
        log.WriteLine("first");
        await using (var other = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            await other.WriteAsync("second\n"u8.ToArray());
        }

        log.WriteLine("third");
        var lines = new List<string>();
        log.ReadLines(lines.Add);
        Assert.Equal(["first", "second", "third"], lines);
    }

    [Fact]
    public void Time_zones_resolve_iana_names_on_every_platform()
    {
        var zones = new IanaTimeZoneResolver();
        Assert.True(zones.TryFind("Europe/London", out _));
        Assert.True(zones.TryFind("UTC", out _));
        Assert.False(zones.TryFind("Not/A_Real_Zone", out _));
    }

    [Fact]
    public async Task Sqlite_constraint_errors_are_recognised()
    {
        using var fixture = new StoreFixture();
        await fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "alice", "x", "admin", true));
        var error = await Assert.ThrowsAsync<SqliteException>(() => fixture.WithUnitOfWork(uow => fixture.Users.InsertUserAsync(uow, "ALICE", "x", "admin", true)));
        Assert.True(SqliteValues.IsIntegrityError(error));
    }

    [Fact]
    public void Timestamps_round_trip_through_the_fixed_sqlite_storage_and_wire_text_formats()
    {
        var value = Timestamp.FromUtc(new DateTime(2026, 9, 17, 2, 19, 51, DateTimeKind.Utc).AddTicks(2553240));
        Assert.Equal("2026-09-17 02:19:51.255324", value.ToSqlite());
        Assert.True(Timestamp.TryFromIsoFormat(value.ToSqlite(), out var read));
        Assert.Equal("2026-09-17T02:19:51.255324", read.ToWireText());
        Assert.Equal("2026-09-17T02:19:51.255324Z", value.ToWireText());
        Assert.Equal("2026-09-17T02:19:51.255324+00:00", value.IsoFormat());
        Assert.True(Timestamp.TryFromIsoFormat("2026-09-17 02:19:51", out var whole));
        Assert.Equal("2026-09-17T02:19:51", whole.ToWireText());
    }
}
