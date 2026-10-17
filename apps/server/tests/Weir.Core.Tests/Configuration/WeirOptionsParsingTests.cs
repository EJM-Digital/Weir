using Weir.Core.Configuration;

namespace Weir.Core.Tests.Configuration;

/// <summary>How each WEIR_* variable is read, clamped and validated, matching <c>WeirSettings.load()</c>.</summary>
public sealed class WeirOptionsParsingTests
{
    [Fact]
    public void The_manager_workflow_sync_is_on_unless_switched_off()
    {
        Assert.True(TestRuntime.Load().ManagerWorkflowSyncEnabled);
        Assert.False(TestRuntime.Load(("WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED", "0")).ManagerWorkflowSyncEnabled);
    }

    [Theory]
    [InlineData(" Production ", "production")]
    [InlineData("", "production")]
    [InlineData("   ", "")]
    public void Env_is_trimmed_and_lower_cased(string raw, string expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_ENV", raw)).Env);

    [Fact]
    public void Env_defaults_to_production_when_unset() =>
        Assert.Equal("production", TestRuntime.Load().Env);

    [Theory]
    [InlineData(" debug ", "debug")]
    [InlineData("", "INFO")]
    [InlineData("   ", "INFO")]
    public void Log_level_is_trimmed_with_info_fallback(string raw, string expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_LOG_LEVEL", raw)).LogLevel);

    [Fact]
    public void Secrets_are_trimmed_and_blank_means_unset()
    {
        var options = TestRuntime.Load(
            ("WEIR_SESSION_SECRET", "  s3cret  "),
            ("WEIR_CREDENTIALS_SECRET", "   "),
            ("WEIR_METRICS_BEARER_TOKEN", " token "));
        Assert.Equal("s3cret", options.SessionSecret);
        Assert.Null(options.CredentialsSecret);
        Assert.Equal("token", options.MetricsBearerToken);
    }

    [Fact]
    public void Previous_credentials_secrets_drop_blanks_and_the_current_secret()
    {
        var options = TestRuntime.Load(
            ("WEIR_CREDENTIALS_SECRET", "current"),
            ("WEIR_PREVIOUS_CREDENTIALS_SECRETS", " old1 , ,current, old2 "));
        Assert.Equal(["old1", "old2"], options.PreviousCredentialsSecrets);
    }

    [Theory]
    [InlineData("  my_cookie ", "my_cookie")]
    [InlineData("   ", "weir_session")]
    public void Cookie_name(string raw, string expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_SESSION_COOKIE_NAME", raw)).SessionCookieName);

    [Theory]
    [InlineData("Strict", CookieSameSite.Strict)]
    [InlineData(" none ", CookieSameSite.None)]
    [InlineData("lax", CookieSameSite.Lax)]
    [InlineData("bogus", CookieSameSite.Lax)]
    [InlineData("", CookieSameSite.Lax)]
    public void Cookie_samesite(string raw, CookieSameSite expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_SESSION_COOKIE_SAMESITE", raw)).SessionCookieSameSite);

    [Theory]
    [InlineData("1", CookieSecureMode.Always)]
    [InlineData("TRUE", CookieSecureMode.Always)]
    [InlineData("yes", CookieSecureMode.Always)]
    [InlineData("on", CookieSecureMode.Always)]
    [InlineData("always", CookieSecureMode.Always)]
    [InlineData("0", CookieSecureMode.Never)]
    [InlineData("false", CookieSecureMode.Never)]
    [InlineData("no", CookieSecureMode.Never)]
    [InlineData("off", CookieSecureMode.Never)]
    [InlineData(" never ", CookieSecureMode.Never)]
    [InlineData("auto", CookieSecureMode.Auto)]
    [InlineData("maybe", CookieSecureMode.Auto)]
    public void Cookie_secure_mode(string raw, CookieSecureMode expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_SESSION_COOKIE_SECURE", raw)).SessionCookieSecureMode);

    [Fact]
    public void Session_lifetimes_have_a_floor_of_one()
    {
        var options = TestRuntime.Load(
            ("WEIR_SESSION_IDLE_MINUTES", "0"),
            ("WEIR_SESSION_ABSOLUTE_DAYS", "-4"),
            ("WEIR_SESSION_TRUSTED_IDLE_DAYS", "-1"),
            ("WEIR_SESSION_TRUSTED_ABSOLUTE_DAYS", "0"));
        Assert.Equal(1, options.SessionIdleMinutes);
        Assert.Equal(1, options.SessionAbsoluteDays);
        Assert.Equal(1440, options.SessionTrustedIdleMinutes);
        Assert.Equal(1, options.SessionTrustedAbsoluteDays);
    }

    [Fact]
    public void Trusted_idle_days_become_minutes()
    {
        Assert.Equal(2 * 1440, TestRuntime.Load(("WEIR_SESSION_TRUSTED_IDLE_DAYS", "2")).SessionTrustedIdleMinutes);
    }

    [Fact]
    public void Rate_limits_have_a_floor_of_one()
    {
        var options = TestRuntime.Load(
            ("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", "0"),
            ("WEIR_AUTH_LOGIN_RATE_WINDOW_SECONDS", "-1"),
            ("WEIR_BOOTSTRAP_RATE_MAX_ATTEMPTS", "5"),
            ("WEIR_BOOTSTRAP_RATE_WINDOW_SECONDS", "0"));
        Assert.Equal(1, options.AuthLoginRateMaxAttempts);
        Assert.Equal(1, options.AuthLoginRateWindowSeconds);
        Assert.Equal(5, options.BootstrapRateMaxAttempts);
        Assert.Equal(1, options.BootstrapRateWindowSeconds);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(" Yes ", true)]
    [InlineData("on", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("junk", false)]
    public void Hsts_boolean(string raw, bool expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_SECURITY_ENABLE_HSTS", raw)).SecurityEnableHsts);

    [Fact]
    public void Unparseable_booleans_keep_their_default()
    {
        Assert.True(TestRuntime.Load(("WEIR_PROCESSING_WATCHER_ENABLED", "perhaps")).ProcessingWatcherEnabled);
        Assert.False(TestRuntime.Load(("WEIR_PROCESSING_WATCHER_ENABLED", "off")).ProcessingWatcherEnabled);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData(" +7 ", 7)]
    [InlineData("1_0", 10)]
    [InlineData("1.5", 10)]
    [InlineData("ten", 10)]
    [InlineData("", 10)]
    [InlineData("1__0", 10)]
    public void Integers_parse_strictly_and_fall_back_to_the_default(string raw, int expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_PROCESSING_PROBE_SIZE_MB", raw)).ProcessingProbeSizeMb);

    [Fact]
    public void Webhook_secret_is_trimmed_and_read_from_one_name_only()
    {
        Assert.Equal("new", TestRuntime.Load(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", " new ")).MediaManagerWebhookSecret);
        Assert.Null(TestRuntime.Load(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "  ")).MediaManagerWebhookSecret);
        // WEIR_SUBBER_WEBHOOK_SECRET is a retired name. Asserted here so a fallback to it cannot creep
        // back in unnoticed: an install still setting the old name is unauthenticated rather than
        // quietly authenticated.
        Assert.Null(TestRuntime.Load(("WEIR_SUBBER_WEBHOOK_SECRET", "old")).MediaManagerWebhookSecret);
    }

    [Fact]
    public void Cors_wildcard_is_refused_with_the_documented_message()
    {
        var error = Assert.Throws<WeirConfigurationException>(() => TestRuntime.Load(("WEIR_ENV", "production"), ("WEIR_CORS_ORIGINS", "https://a.example, *")));
        Assert.Equal(
            "WEIR_CORS_ORIGINS cannot include '*' because Weir uses credentialed browser requests. Configure explicit origins instead.",
            error.Message);
        Assert.Throws<WeirConfigurationException>(() => TestRuntime.Load(("WEIR_CORS_ORIGINS", "*")));
    }

    [Fact]
    public void Cors_origins_are_split_and_trimmed_without_expansion_outside_development()
    {
        var options = TestRuntime.Load(("WEIR_ENV", "production"), ("WEIR_CORS_ORIGINS", " http://localhost:8782/ , ,https://weir.example "));
        Assert.Equal(["http://localhost:8782/", "https://weir.example"], options.CorsOrigins);
    }

    [Fact]
    public void Development_adds_the_other_loopback_hostname()
    {
        var options = TestRuntime.Load(
            ("WEIR_ENV", "development"),
            ("WEIR_CORS_ORIGINS", "http://localhost:8782/,https://127.0.0.1,http://weir.lan:1"),
            ("WEIR_TRUSTED_BROWSER_ORIGINS", "HTTP://LOCALHOST:9"));
        Assert.Equal(
            ["http://localhost:8782", "http://127.0.0.1:8782", "https://127.0.0.1", "https://localhost", "http://weir.lan:1"],
            options.CorsOrigins);
        Assert.Equal(["HTTP://LOCALHOST:9", "http://127.0.0.1:9"], options.TrustedBrowserOriginsOverride);
    }

    [Fact]
    public void Development_expansion_refuses_a_non_numeric_or_out_of_range_origin()
    {
        var notNumber = Assert.Throws<WeirConfigurationException>(
            () => TestRuntime.Load(("WEIR_ENV", "development"), ("WEIR_CORS_ORIGINS", "http://localhost:abc")));
        Assert.Equal("Port could not be cast to integer value as 'abc'", notNumber.Message);
        var outOfRange = Assert.Throws<WeirConfigurationException>(
            () => TestRuntime.Load(("WEIR_ENV", "development"), ("WEIR_CORS_ORIGINS", "http://localhost:70000")));
        Assert.Equal("Port out of range 0-65535", outOfRange.Message);
    }

    [Fact]
    public void Trusted_browser_origins_fall_back_to_cors()
    {
        Assert.Equal(["https://a.example"], TestRuntime.Load(("WEIR_ENV", "production"), ("WEIR_CORS_ORIGINS", "https://a.example")).TrustedBrowserOrigins);
        Assert.Equal(
            ["https://b.example"],
            TestRuntime.Load(("WEIR_ENV", "production"), ("WEIR_CORS_ORIGINS", "https://a.example"), ("WEIR_TRUSTED_BROWSER_ORIGINS", "https://b.example")).TrustedBrowserOrigins);
    }

    [Fact]
    public void Trusted_proxy_ips_are_a_csv_list()
    {
        Assert.Equal(["10.0.0.0/8", "::1"], TestRuntime.Load(("WEIR_TRUSTED_PROXY_IPS", "10.0.0.0/8, ::1,")).TrustedProxyIps);
    }

    [Theory]
    [InlineData("-3", 1)]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("99", 10)]
    public void Processing_worker_count_is_zero_to_ten_and_negative_means_one(string raw, int expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_PROCESSING_WORKER_COUNT", raw)).ProcessingWorkerCount);

    [Theory]
    [InlineData("1", 30)]
    [InlineData("0", 30)]
    [InlineData("-5", 30)]
    [InlineData("120", 120)]
    [InlineData("999999", 86_400)]
    public void Processing_job_lease_seconds_is_thirty_seconds_to_a_day(string raw, int expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_PROCESSING_JOB_LEASE_SECONDS", raw)).ProcessingJobLeaseSeconds);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("45", 45)]
    [InlineData("999999", 120)]
    public void Sqlite_busy_timeout_seconds_is_one_second_to_two_minutes(string raw, int expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_SQLITE_BUSY_TIMEOUT_SECONDS", raw)).SqliteBusyTimeoutSeconds);

    [Fact]
    public void Sqlite_busy_timeout_is_thirty_seconds_unless_set() =>
        Assert.Equal(30, TestRuntime.Load().SqliteBusyTimeoutSeconds);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_library_swap_is_held_nowhere_unless_a_point_is_named(string? raw) =>
        Assert.Null(raw is null ? TestRuntime.Load().LibrarySwapHoldAt : TestRuntime.Load(("WEIR_LIBRARY_SWAP_HOLD_AT", raw)).LibrarySwapHoldAt);

    [Fact]
    public void A_library_swap_hold_keeps_the_point_as_written() =>
        Assert.Equal(
            "OriginalMovedAside",
            TestRuntime.Load(("WEIR_ENV", "development"), ("WEIR_LIBRARY_SWAP_HOLD_AT", " OriginalMovedAside ")).LibrarySwapHoldAt);

    [Theory]
    [InlineData(null)]
    [InlineData("production")]
    [InlineData(" Production ")]
    public void A_library_swap_hold_is_refused_at_start_in_production(string? env)
    {
        var refused = Assert.Throws<WeirConfigurationException>(() => env is null
            ? TestRuntime.Load(("WEIR_LIBRARY_SWAP_HOLD_AT", "OriginalMovedAside"))
            : TestRuntime.Load(("WEIR_ENV", env), ("WEIR_LIBRARY_SWAP_HOLD_AT", "OriginalMovedAside")));

        Assert.Equal(WeirOptionsLoader.LibrarySwapHoldInProductionMessage, refused.Message);
    }

    [Fact]
    public void A_library_swap_hold_left_empty_in_production_is_not_a_hold_and_starts() =>
        Assert.Null(TestRuntime.Load(("WEIR_ENV", "production"), ("WEIR_LIBRARY_SWAP_HOLD_AT", "")).LibrarySwapHoldAt);

    [Fact]
    public void Periodic_tasks_run_at_their_own_pace_unless_a_test_speeds_them_up() =>
        Assert.Equal(1, TestRuntime.Load().PeriodicTaskSpeedUp);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("5000", 1000)]
    public void The_periodic_task_speed_up_is_a_whole_number_from_one_to_a_thousand(string raw, int expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_ENV", "development"), ("WEIR_PERIODIC_TASK_SPEEDUP", raw)).PeriodicTaskSpeedUp);

    [Theory]
    [InlineData(null)]
    [InlineData("production")]
    [InlineData(" Production ")]
    public void A_periodic_task_speed_up_is_refused_at_start_in_production(string? env)
    {
        var refused = Assert.Throws<WeirConfigurationException>(() => env is null
            ? TestRuntime.Load(("WEIR_PERIODIC_TASK_SPEEDUP", "10"))
            : TestRuntime.Load(("WEIR_ENV", env), ("WEIR_PERIODIC_TASK_SPEEDUP", "10")));

        Assert.Equal(WeirOptionsLoader.PeriodicTaskSpeedUpInProductionMessage, refused.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_release_feed_is_played_unless_a_test_names_one(string? raw) =>
        Assert.Null(TestRuntime.Load(("WEIR_ENV", "development"), ("WEIR_RELEASES_FEED_URL", raw ?? string.Empty)).ReleasesFeedUrl);

    [Theory]
    [InlineData("http://localhost:5050/releases.atom")]
    [InlineData("  https://example.test/feed.atom  ", "https://example.test/feed.atom")]
    public void A_release_feed_a_test_names_is_read_as_given(string raw, string? expected = null) =>
        Assert.Equal(expected ?? raw, TestRuntime.Load(("WEIR_ENV", "development"), ("WEIR_RELEASES_FEED_URL", raw)).ReleasesFeedUrl);

    [Theory]
    [InlineData("releases.atom")]
    [InlineData("ftp://example.test/releases.atom")]
    [InlineData("http://")]
    public void A_release_feed_that_is_not_an_http_address_refuses_to_start(string raw)
    {
        var refused = Assert.Throws<WeirConfigurationException>(() => TestRuntime.Load(("WEIR_ENV", "development"), ("WEIR_RELEASES_FEED_URL", raw)));

        Assert.Equal(WeirOptionsLoader.ReleasesFeedNotAnAddressMessage, refused.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("production")]
    [InlineData(" Production ")]
    public void A_release_feed_is_refused_at_start_in_production(string? env)
    {
        var refused = Assert.Throws<WeirConfigurationException>(() => env is null
            ? TestRuntime.Load(("WEIR_RELEASES_FEED_URL", "http://localhost:5050/releases.atom"))
            : TestRuntime.Load(("WEIR_ENV", env), ("WEIR_RELEASES_FEED_URL", "http://localhost:5050/releases.atom")));

        Assert.Equal(WeirOptionsLoader.ReleasesFeedInProductionMessage, refused.Message);
    }

    [Fact]
    public void A_periodic_task_speed_up_of_one_is_no_speed_up_and_starts_in_production() =>
        Assert.Equal(1, TestRuntime.Load(("WEIR_ENV", "production"), ("WEIR_PERIODIC_TASK_SPEEDUP", "1")).PeriodicTaskSpeedUp);

    [Theory]
    [InlineData("0", 0.25)]
    [InlineData("10", 10.0)]
    [InlineData("1000", 300.0)]
    [InlineData("2.5", 3.0)]
    public void Watcher_debounce_is_a_whole_number_clamped_to_a_quarter_second_through_five_minutes(string raw, double expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", raw)).ProcessingWatcherDebounceSeconds);

    [Fact]
    public void Processing_ranges_are_clamped()
    {
        var low = TestRuntime.Load(
            ("WEIR_PROCESSING_PROBE_SIZE_MB", "0"),
            ("WEIR_PROCESSING_ANALYZE_DURATION_SECONDS", "0"),
            ("WEIR_PROCESSING_MOVIE_OUTPUT_CLEANUP_MIN_AGE_SECONDS", "1"),
            ("WEIR_PROCESSING_TV_OUTPUT_CLEANUP_MIN_AGE_SECONDS", "1"),
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MIN_STALE_AGE_SECONDS", "1"),
            ("WEIR_JOB_ROWS_RETENTION_DAYS", "0"),
            ("WEIR_JOB_ROWS_RETENTION_SCHEDULE_INTERVAL_SECONDS", "1"));
        Assert.Equal(1, low.ProcessingProbeSizeMb);
        Assert.Equal(1, low.ProcessingAnalyzeDurationSeconds);
        Assert.Equal(3600, low.ProcessingMovieOutputCleanupMinAgeSeconds);
        Assert.Equal(3600, low.ProcessingTvOutputCleanupMinAgeSeconds);
        Assert.Equal(60, low.ProcessingWorkTempStaleSweepMinStaleAgeSeconds);
        Assert.Equal(1, low.JobRowsRetentionDays);
        Assert.Equal(60, low.JobRowsRetentionScheduleIntervalSeconds);

        var high = TestRuntime.Load(
            ("WEIR_PROCESSING_PROBE_SIZE_MB", "99999"),
            ("WEIR_PROCESSING_ANALYZE_DURATION_SECONDS", "99999"),
            ("WEIR_PROCESSING_MOVIE_OUTPUT_CLEANUP_MIN_AGE_SECONDS", "99999999"),
            ("WEIR_PROCESSING_TV_OUTPUT_CLEANUP_MIN_AGE_SECONDS", "99999999"),
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MIN_STALE_AGE_SECONDS", "99999999"),
            ("WEIR_JOB_ROWS_RETENTION_DAYS", "99999"),
            ("WEIR_JOB_ROWS_RETENTION_SCHEDULE_INTERVAL_SECONDS", "99999999"));
        Assert.Equal(1024, high.ProcessingProbeSizeMb);
        Assert.Equal(300, high.ProcessingAnalyzeDurationSeconds);
        Assert.Equal(30 * 24 * 3600, high.ProcessingMovieOutputCleanupMinAgeSeconds);
        Assert.Equal(30 * 24 * 3600, high.ProcessingTvOutputCleanupMinAgeSeconds);
        Assert.Equal(30 * 24 * 3600, high.ProcessingWorkTempStaleSweepMinStaleAgeSeconds);
        Assert.Equal(365, high.JobRowsRetentionDays);
        Assert.Equal(86400, high.JobRowsRetentionScheduleIntervalSeconds);
    }

    [Fact]
    public void Processing_toggles_are_read()
    {
        var options = TestRuntime.Load(
            ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_SCHEDULE_ENABLED", "false"),
            ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_PERIODIC_ENQUEUE_REMUX_JOBS", "false"));
        Assert.False(options.ProcessingWatchedFolderRemuxScanDispatchScheduleEnabled);
        Assert.False(options.ProcessingWatchedFolderRemuxScanDispatchPeriodicEnqueueRemuxJobs);
    }

    [Fact]
    public void The_variables_of_the_removed_failed_download_cleanup_are_accepted_and_change_nothing()
    {
        var withRetired = TestRuntime.Load(
            ("WEIR_PROCESSING_MOVIE_FAILURE_CLEANUP_SCHEDULE_ENABLED", "true"),
            ("WEIR_PROCESSING_MOVIE_FAILURE_CLEANUP_SCHEDULE_INTERVAL_SECONDS", "120"),
            ("WEIR_PROCESSING_TV_FAILURE_CLEANUP_SCHEDULE_ENABLED", "1"),
            ("WEIR_PROCESSING_TV_FAILURE_CLEANUP_SCHEDULE_INTERVAL_SECONDS", "120"),
            ("WEIR_PROCESSING_MOVIE_FAILURE_CLEANUP_GRACE_PERIOD_SECONDS", "600"),
            ("WEIR_PROCESSING_TV_FAILURE_CLEANUP_GRACE_PERIOD_SECONDS", "600"));

        Assert.Equivalent(TestRuntime.Load(), withRetired);
    }

    [Fact]
    public void Temp_sweep_schedule_reads_only_the_per_scope_variables()
    {
        // The shared WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_SCHEDULE_* pair is retired in favour of the
        // per-scope variables. Setting only the shared pair changes nothing, which is the assertion
        // worth keeping.
        var shared = TestRuntime.Load(
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_SCHEDULE_ENABLED", "true"),
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_SCHEDULE_INTERVAL_SECONDS", "7200"));
        Assert.False(shared.ProcessingWorkTempStaleSweepMovieScheduleEnabled);
        Assert.False(shared.ProcessingWorkTempStaleSweepTvScheduleEnabled);
        Assert.Equal(3600, shared.ProcessingWorkTempStaleSweepMovieScheduleIntervalSeconds);
        Assert.Equal(3600, shared.ProcessingWorkTempStaleSweepTvScheduleIntervalSeconds);

        var specific = TestRuntime.Load(
            // Present but empty is still "set", as `key in os.environ` is, and parses to false.
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_ENABLED", ""),
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_ENABLED", "true"),
            ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_INTERVAL_SECONDS", "30"));
        Assert.False(specific.ProcessingWorkTempStaleSweepMovieScheduleEnabled);
        Assert.True(specific.ProcessingWorkTempStaleSweepTvScheduleEnabled);
        Assert.Equal(3600, specific.ProcessingWorkTempStaleSweepMovieScheduleIntervalSeconds);
        Assert.Equal(60, specific.ProcessingWorkTempStaleSweepTvScheduleIntervalSeconds);
    }

    [Fact]
    public void Arr_base_urls_must_be_http_and_keys_are_trimmed()
    {
        var options = TestRuntime.Load(
            ("WEIR_ARR_RADARR_BASE_URL", " http://radarr:7878 "),
            ("WEIR_ARR_RADARR_API_KEY", " key1 "),
            ("WEIR_ARR_SONARR_BASE_URL", "sonarr:8989"),
            ("WEIR_ARR_SONARR_API_KEY", " "));
        Assert.Equal("http://radarr:7878", options.ArrRadarrBaseUrl);
        Assert.Equal("key1", options.ArrRadarrApiKey);
        Assert.Null(options.ArrSonarrBaseUrl);
        Assert.Null(options.ArrSonarrApiKey);
        Assert.Null(TestRuntime.Load(("WEIR_ARR_RADARR_BASE_URL", "HTTP://upper")).ArrRadarrBaseUrl);
    }

    [Fact]
    public void Path_overrides_resolve_relative_to_home_and_expand_tilde()
    {
        var runtime = TestRuntime.With(
            ("WEIR_DB_PATH", "db/custom.sqlite3"),
            ("WEIR_BACKUP_DIR", "~/weir-backups"),
            ("WEIR_LOG_DIR", Path.Join(Path.GetTempPath(), "abs-logs") + Path.DirectorySeparatorChar),
            ("WEIR_TEMP_DIR", "  "));
        var options = WeirOptionsLoader.Load(runtime);
        Assert.Equal(Path.GetFullPath(Path.Join(options.WeirHome, "db", "custom.sqlite3")), options.DbPath);
        Assert.Equal(Path.GetFullPath(Path.Join(runtime.UserHomeDirectory, "weir-backups")), options.BackupDir);
        Assert.Equal(Path.GetFullPath(Path.Join(Path.GetTempPath(), "abs-logs")), options.LogDir);
        Assert.Equal(Path.GetFullPath(Path.Join(options.WeirHome, "temp")), options.TempDir);
    }

    [Fact]
    public void Relative_home_resolves_against_the_working_directory()
    {
        var runtime = TestRuntime.With(("WEIR_HOME", " relative-home "));
        Assert.Equal(Path.GetFullPath(Path.Join(runtime.CurrentDirectory, "relative-home")), WeirOptionsLoader.Load(runtime).WeirHome);
    }

    [Fact]
    public void Web_dist_and_version_override()
    {
        var runtime = TestRuntime.With(("WEIR_WEB_DIST", " ~/web/dist/ "), ("WEIR_VERSION", " 9.9.9 "));
        var options = WeirOptionsLoader.Load(runtime);
        Assert.Equal(Path.GetFullPath(Path.Join(runtime.UserHomeDirectory, "web", "dist")), options.WebDist);
        Assert.Equal("9.9.9", options.VersionOverride);
        Assert.Equal("9.9.9", WeirVersion.Resolve(options.VersionOverride));
        Assert.Equal(WeirVersion.BuildVersion, WeirVersion.Resolve(null));
    }

    /// <summary>#555: WEIR_CHOWN_OUTPUT and the WEIR_PUID/WEIR_PGID target it applies to.</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("sometimes", false)]
    public void Chown_output_is_boolish_with_a_false_default(string raw, bool expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_CHOWN_OUTPUT", raw)).OutputOwnershipChownEnabled);

    [Fact]
    public void Output_ownership_uid_and_gid_prefer_the_weir_prefixed_variable()
    {
        var options = TestRuntime.Load(("WEIR_PUID", "2000"), ("PUID", "3000"), ("WEIR_PGID", "2001"), ("PGID", "3001"));
        Assert.Equal(2000u, options.OutputOwnershipUid);
        Assert.Equal(2001u, options.OutputOwnershipGid);
    }

    [Fact]
    public void Output_ownership_uid_and_gid_fall_back_to_the_unprefixed_variable()
    {
        var options = TestRuntime.Load(("PUID", "3000"), ("PGID", "3001"));
        Assert.Equal(3000u, options.OutputOwnershipUid);
        Assert.Equal(3001u, options.OutputOwnershipGid);
    }

    [Theory]
    [InlineData("", 1000u)]
    [InlineData("   ", 1000u)]
    [InlineData("not-a-number", 1000u)]
    public void Output_ownership_uid_defaults_to_1000_when_unset_or_unparseable(string raw, uint expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_PUID", raw)).OutputOwnershipUid);

    /// <summary>A parsed-but-negative id floors at 0, the same clamp every other id-shaped setting here uses.</summary>
    [Fact]
    public void Output_ownership_uid_floors_negative_values_at_zero() =>
        Assert.Equal(0u, TestRuntime.Load(("WEIR_PUID", "-5")).OutputOwnershipUid);

    /// <summary>#555: WEIR_FILE_MODE_OUTPUT / WEIR_DIR_MODE_OUTPUT, octal strings such as 664, 775 or the setgid form 2775.</summary>
    [Theory]
    [InlineData("664", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead)]
    [InlineData(
        "775",
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute)]
    [InlineData(
        "2775",
        UnixFileMode.SetGroup |
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute)]
    public void Output_dir_mode_parses_the_octal_string(string raw, UnixFileMode expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_DIR_MODE_OUTPUT", raw)).OutputOwnershipDirectoryMode);

    [Fact]
    public void Output_file_mode_parses_independently_of_the_directory_mode()
    {
        var options = TestRuntime.Load(("WEIR_FILE_MODE_OUTPUT", "660"), ("WEIR_DIR_MODE_OUTPUT", "770"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite, options.OutputOwnershipFileMode);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute,
            options.OutputOwnershipDirectoryMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Output_modes_are_unset_when_blank(string raw)
    {
        var options = TestRuntime.Load(("WEIR_FILE_MODE_OUTPUT", raw), ("WEIR_DIR_MODE_OUTPUT", raw));
        Assert.Null(options.OutputOwnershipFileMode);
        Assert.Null(options.OutputOwnershipDirectoryMode);
    }

    [Theory]
    [InlineData("77")]
    [InlineData("77777")]
    [InlineData("27x5")]
    [InlineData("999")]
    [InlineData("-775")]
    public void A_malformed_output_dir_mode_refuses_to_start(string raw)
    {
        var error = Assert.Throws<WeirConfigurationException>(() => TestRuntime.Load(("WEIR_DIR_MODE_OUTPUT", raw)));
        Assert.Equal("WEIR_DIR_MODE_OUTPUT must be an octal file mode such as 664 or 775.", error.Message);
    }

    [Fact]
    public void A_malformed_output_file_mode_refuses_to_start()
    {
        var error = Assert.Throws<WeirConfigurationException>(() => TestRuntime.Load(("WEIR_FILE_MODE_OUTPUT", "abc")));
        Assert.Equal("WEIR_FILE_MODE_OUTPUT must be an octal file mode such as 664 or 775.", error.Message);
    }

    [Fact]
    public void The_artwork_gateway_is_the_real_service_unless_set() =>
        Assert.Equal("https://deluno-metadata-gateway.ejmdigital.workers.dev", TestRuntime.Load().ArtworkGatewayUrl);

    [Theory]
    [InlineData(" http://localhost:9000/ ", "http://localhost:9000")]
    [InlineData("https://metadata.example/base", "https://metadata.example/base")]
    public void A_configured_artwork_gateway_is_trimmed_without_a_trailing_slash(string raw, string expected) =>
        Assert.Equal(expected, TestRuntime.Load(("WEIR_ARTWORK_GATEWAY_URL", raw)).ArtworkGatewayUrl);

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    public void Off_switches_poster_lookups_off_for_the_process(string raw) =>
        Assert.Equal(string.Empty, TestRuntime.Load(("WEIR_ARTWORK_GATEWAY_URL", raw)).ArtworkGatewayUrl);

    [Theory]
    [InlineData("metadata.example")]
    [InlineData("ftp://metadata.example")]
    public void An_artwork_gateway_that_is_not_an_http_address_refuses_to_start(string raw)
    {
        var error = Assert.Throws<WeirConfigurationException>(() => TestRuntime.Load(("WEIR_ARTWORK_GATEWAY_URL", raw)));
        Assert.Equal("WEIR_ARTWORK_GATEWAY_URL must be an http or https address, or off.", error.Message);
    }

    [Theory]
    [InlineData("WEIR_HOME")]
    [InlineData("WEIR_DB_PATH")]
    [InlineData("WEIR_BACKUP_DIR")]
    [InlineData("WEIR_LOG_DIR")]
    [InlineData("WEIR_TEMP_DIR")]
    [InlineData("WEIR_WEB_DIST")]
    public void A_folder_setting_the_system_cannot_use_refuses_the_start_naming_the_setting(string variable)
    {
        var refusal = Assert.Throws<WeirConfigurationException>(() => TestRuntime.Load((variable, "a\0b")));

        Assert.Contains(variable, refusal.Message, StringComparison.Ordinal);
    }
}
