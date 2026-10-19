using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing.RemuxPass;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// What the owner can set in the environment reaches what Weir does: the mode it runs in, how much it logs, where it keeps its files,
/// what the sign-in cookie is called and how long a sign-in lasts, and the numbers the clean-up and probing read.
/// </summary>
public sealed class EnvironmentSettingsApiTests
{
    private static string Stamp(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    private static async Task<(WeirTestServer Server, ApiTestClient Admin)> SignedInAsync(params (string Name, string Value)[] variables)
    {
        var server = await StartServerAsync(variables);
        await TestDatabase.SeedAdminAsync(server);
        var admin = new ApiTestClient(server);
        await admin.SignInAsync();
        return (server, admin);
    }

    [Theory]
    [InlineData(null, "Production")]
    [InlineData("production", "Production")]
    [InlineData("development", "Development")]
    public async Task WEIR_ENV_chooses_the_mode_Weir_runs_in_and_an_unset_one_is_production(string? value, string expected)
    {
        await using var server = await StartServerAsync(value is null ? [] : [("WEIR_ENV", value)]);

        Assert.Equal(expected, server.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("development")]
    public async Task A_failure_inside_Weir_answers_the_same_plain_500_in_either_mode_with_nothing_of_the_failure_in_it(string env)
    {
        var (server, admin) = await SignedInAsync(("WEIR_ENV", env));
        await using var _ = server;
        await TestDatabase.ExecuteAsync(server, "PRAGMA foreign_keys=OFF; DROP TABLE suite_configuration_backup");

        using var response = await admin.GetAsync("/api/v1/suite/configuration-backups");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("internal_error", body["code"]!.GetValue<string>());
        Assert.DoesNotContain("suite_configuration_backup", body.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite", body.ToJsonString(), StringComparison.Ordinal);
    }

    private static string LogText(WeirTestServer server)
    {
        var path = Path.Join(server.Home, "logs", "weir.log");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task WEIR_LOG_LEVEL_WARNING_keeps_warnings_in_the_log_and_leaves_information_out()
    {
        await using var server = await StartServerAsync(("WEIR_LOG_LEVEL", "WARNING"));
        var logger = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Weir.Tests.LogLevel");

        logger.LogInformation("an ordinary thing happened");
        logger.LogWarning("something needs a look");

        await Eventually.ThatAsync(() => LogText(server).Contains("something needs a look", StringComparison.Ordinal));
        var text = LogText(server);
        Assert.DoesNotContain("an ordinary thing happened", text, StringComparison.Ordinal);
        Assert.DoesNotContain("is starting", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_WEIR_LOG_LEVEL_information_is_in_the_log_too()
    {
        await using var server = await StartServerAsync();
        var logger = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Weir.Tests.LogLevel");

        logger.LogInformation("an ordinary thing happened");

        await Eventually.ThatAsync(() => LogText(server).Contains("an ordinary thing happened", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_custom_session_cookie_name_is_set_on_sign_in_and_read_back_to_keep_the_person_signed_in()
    {
        var (server, admin) = await SignedInAsync(("WEIR_SESSION_COOKIE_NAME", "my_weir"));
        await using var _ = server;

        Assert.Contains("my_weir", admin.Cookies.Keys);
        Assert.DoesNotContain("weir_session", admin.Cookies.Keys);
        using var me = await admin.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var logout = await admin.PostAsync("/api/v1/auth/logout", new { csrf_token = await admin.CsrfAsync() });
        Assert.StartsWith("my_weir=", Header(logout, "Set-Cookie"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "lax")]
    [InlineData("strict", "strict")]
    [InlineData("none", "none")]
    [InlineData("anything else", "lax")]
    public async Task The_session_cookie_carries_the_SameSite_value_the_environment_names(string? value, string expected)
    {
        await using var server = await StartServerAsync(value is null ? [] : [("WEIR_SESSION_COOKIE_SAMESITE", value)]);
        await TestDatabase.SeedAdminAsync(server);

        using var login = await new ApiTestClient(server).LoginAsync();

        Assert.EndsWith("; SameSite=" + expected, Header(login, "Set-Cookie"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSite_none_on_a_plain_http_install_is_not_marked_Secure_which_a_browser_will_not_keep_unless_Secure_is_switched_on()
    {
        await using var plain = await StartServerAsync(("WEIR_SESSION_COOKIE_SAMESITE", "none"));
        await TestDatabase.SeedAdminAsync(plain);
        await using var secured = await StartServerAsync(("WEIR_SESSION_COOKIE_SAMESITE", "none"), ("WEIR_SESSION_COOKIE_SECURE", "always"));
        await TestDatabase.SeedAdminAsync(secured);

        using var withoutSecure = await new ApiTestClient(plain).LoginAsync();
        using var withSecure = await new ApiTestClient(secured).LoginAsync();

        Assert.DoesNotContain("Secure", Header(withoutSecure, "Set-Cookie"), StringComparison.Ordinal);
        Assert.EndsWith("; SameSite=none; Secure", Header(withSecure, "Set-Cookie"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_lasts_as_long_as_the_absolute_days_for_its_kind_say()
    {
        await using var server = await StartServerAsync(("WEIR_SESSION_ABSOLUTE_DAYS", "3"), ("WEIR_SESSION_TRUSTED_ABSOLUTE_DAYS", "4"));
        await TestDatabase.SeedAdminAsync(server);

        using var plain = await new ApiTestClient(server).LoginAsync();
        using var trusted = await new ApiTestClient(server).LoginAsync(trustedDevice: true);

        Assert.Contains("Max-Age=259200;", Header(plain, "Set-Cookie"), StringComparison.Ordinal);
        Assert.Contains("Max-Age=345600;", Header(trusted, "Set-Cookie"), StringComparison.Ordinal);
        Assert.Equal(3 * 1440, await TestDatabase.ScalarAsync(server, "SELECT CAST(round((julianday(absolute_expires_at) - julianday(created_at)) * 1440) AS INTEGER) FROM user_sessions WHERE is_trusted_device = 0"));
        Assert.Equal(4 * 1440, await TestDatabase.ScalarAsync(server, "SELECT CAST(round((julianday(absolute_expires_at) - julianday(created_at)) * 1440) AS INTEGER) FROM user_sessions WHERE is_trusted_device = 1"));
    }

    [Fact]
    public async Task A_sign_in_that_has_been_idle_too_long_ends_by_the_idle_setting_for_its_kind()
    {
        await using var server = await StartServerAsync(("WEIR_SESSION_IDLE_MINUTES", "5"), ("WEIR_SESSION_TRUSTED_IDLE_DAYS", "2"));
        await TestDatabase.SeedAdminAsync(server);
        var plain = new ApiTestClient(server);
        var trusted = new ApiTestClient(server);
        await plain.SignInAsync();
        using (var login = await trusted.LoginAsync(trustedDevice: true))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE user_sessions SET last_seen_at = $seen", ("$seen", Stamp(DateTimeOffset.UtcNow.AddMinutes(-10))));
        using var plainAfterTenMinutes = await plain.GetAsync("/api/v1/auth/me");
        using var trustedAfterTenMinutes = await trusted.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, plainAfterTenMinutes.StatusCode);
        Assert.Equal(HttpStatusCode.OK, trustedAfterTenMinutes.StatusCode);

        await TestDatabase.ExecuteAsync(server, "UPDATE user_sessions SET last_seen_at = $seen WHERE revoked_at IS NULL", ("$seen", Stamp(DateTimeOffset.UtcNow.AddDays(-3))));
        using var trustedAfterThreeDays = await trusted.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, trustedAfterThreeDays.StatusCode);
    }

    [Fact]
    public async Task The_log_the_backups_the_temp_folder_and_the_database_are_where_the_environment_puts_them()
    {
        await using var server = await WeirTestServer.StartAsync(
            [
                ("WEIR_SESSION_SECRET", Secret),
                ("WEIR_PROCESSING_WORKER_COUNT", "0"),
                ("WEIR_LOG_DIR", "{home}/elsewhere/logs"),
                ("WEIR_BACKUP_DIR", "{home}/elsewhere/backups"),
                ("WEIR_TEMP_DIR", "{home}/elsewhere/temp"),
                ("WEIR_DB_PATH", "{home}/elsewhere/db/weir.sqlite3"),
            ],
            freshDatabase: true);

        Assert.True(File.Exists(Path.Join(server.Home, "elsewhere", "logs", "weir.log")));
        Assert.True(File.Exists(Path.Join(server.Home, "elsewhere", "db", "weir.sqlite3")));
        Assert.True(Directory.Exists(Path.Join(server.Home, "elsewhere", "temp")));
        Assert.True(Directory.Exists(Path.Join(server.Home, "elsewhere", "backups")));
        Assert.False(Directory.Exists(Path.Join(server.Home, "logs")));
        Assert.False(Directory.Exists(Path.Join(server.Home, "temp")));
        Assert.False(Directory.Exists(Path.Join(server.Home, "backups")));
        Assert.False(File.Exists(Path.Join(server.Home, "data", "weir.sqlite3")));
    }

    [Fact]
    public async Task A_backup_taken_now_goes_in_the_folder_WEIR_BACKUP_DIR_names()
    {
        var (server, admin) = await SignedInAsync(("WEIR_BACKUP_DIR", "{home}/my-backups"));
        await using var _ = server;

        using var created = await admin.PostAsync("/api/v1/suite/configuration-backups", new { csrf_token = await admin.CsrfAsync() });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = Directory.GetFiles(Path.Join(server.Home, "my-backups", "suite-configuration"), "suite-configuration-*.json");
        Assert.Single(saved);
        Assert.False(Directory.Exists(Path.Join(server.Home, "backups", "suite-configuration")) && Directory.GetFiles(Path.Join(server.Home, "backups", "suite-configuration")).Length > 0);
    }

    private static TimeSpan SweepEvery(WeirTestServer server, string timerName) =>
        server.Services.GetServices<IPeriodicEnqueuer>().Single(enqueuer => enqueuer.Name == timerName).Interval;

    [Fact]
    public async Task Each_work_file_sweep_takes_its_own_interval_from_the_environment()
    {
        await using var server = await StartServerAsync(
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_INTERVAL_SECONDS", "120"),
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_INTERVAL_SECONDS", "7200"));

        Assert.Equal(TimeSpan.FromSeconds(120), SweepEvery(server, "work temp stale sweep (movie)"));
        Assert.Equal(TimeSpan.FromSeconds(7200), SweepEvery(server, "work temp stale sweep (tv)"));
    }

    [Fact]
    public async Task A_sweep_left_to_its_default_runs_hourly_whatever_the_other_one_is_set_to()
    {
        await using var server = await StartServerAsync(("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_INTERVAL_SECONDS", "120"));

        Assert.Equal(TimeSpan.FromSeconds(120), SweepEvery(server, "work temp stale sweep (movie)"));
        Assert.Equal(TimeSpan.FromHours(1), SweepEvery(server, "work temp stale sweep (tv)"));
    }

    [Fact]
    public async Task The_probe_and_clean_up_numbers_in_the_environment_are_the_ones_a_pass_is_given()
    {
        var (server, admin) = await SignedInAsync(
            ("WEIR_PROCESSING_PROBE_SIZE_MB", "25"),
            ("WEIR_PROCESSING_ANALYZE_DURATION_SECONDS", "14"),
            ("WEIR_PROCESSING_MOVIE_OUTPUT_CLEANUP_MIN_AGE_SECONDS", "7200"),
            ("WEIR_PROCESSING_TV_OUTPUT_CLEANUP_MIN_AGE_SECONDS", "10800"));
        await using var _ = server;

        var settings = server.Services.GetRequiredService<RemuxPassSettings>();
        var shown = await Json(await admin.GetAsync("/api/v1/processing/runtime-settings"));

        Assert.Equal((25, 14, 7200, 10800), (settings.ProbeSizeMb, settings.AnalyzeDurationSeconds, settings.MovieOutputCleanupMinAgeSeconds, settings.TvOutputCleanupMinAgeSeconds));
        Assert.Equal(25, shown["processing_probe_size_mb"]!.GetValue<int>());
        Assert.Equal(14, shown["processing_analyze_duration_seconds"]!.GetValue<int>());
        Assert.Equal(7200, shown["processing_movie_output_cleanup_min_age_seconds"]!.GetValue<int>());
        Assert.Equal(10800, shown["processing_tv_output_cleanup_min_age_seconds"]!.GetValue<int>());
    }
}
