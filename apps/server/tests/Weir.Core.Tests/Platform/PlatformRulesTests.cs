using Microsoft.Extensions.Time.Testing;
using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Logs;
using Weir.Core.Metrics;
using Weir.Core.Net;
using Weir.Core.Notifications;
using Weir.Core.Observability;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Core.Updates;

namespace Weir.Core.Tests.Platform;

/// <summary>Sessions, rate limits, settings, observability, metrics, logs and updates.</summary>
public sealed class PlatformRulesTests
{
    private static readonly WeirOptions Options = TestRuntime.Load();

    [Theory]
    [InlineData("http", CookieSecureMode.Auto, false)]
    [InlineData("https", CookieSecureMode.Auto, true)]
    [InlineData("http", CookieSecureMode.Always, true)]
    [InlineData("https", CookieSecureMode.Always, true)]
    [InlineData("http", CookieSecureMode.Never, false)]
    [InlineData("https", CookieSecureMode.Never, false)]
    [InlineData("HTTPS", CookieSecureMode.Auto, true)]
    [InlineData("", CookieSecureMode.Auto, false)]
    public void Cookie_secure_flag_follows_the_mode_and_scheme(string scheme, CookieSecureMode mode, bool expected) =>
        Assert.Equal(expected, SessionRules.ResolveCookieSecure(scheme, mode));

    [Theory]
    [InlineData("http", "https://weir.example", null, false, true)]
    [InlineData("http", null, "https://weir.example/login", false, true)]
    [InlineData("http", "http://weir.example", null, false, false)]
    [InlineData("https", "https://weir.example", null, false, false)]
    [InlineData("http", "https://weir.example", null, true, false)]
    [InlineData("http", null, null, false, false)]
    public void Cookie_secure_warning_needs_https_evidence_plain_http_and_no_trusted_proxy(
        string scheme, string? origin, string? referer, bool hasTrustedProxy, bool expected) =>
        Assert.Equal(expected, SessionRules.CookieSecureBlockedByUntranslatedTls(CookieSecureMode.Auto, scheme, origin, referer, hasTrustedProxy));

    [Theory]
    [InlineData(CookieSecureMode.Always)]
    [InlineData(CookieSecureMode.Never)]
    public void Cookie_secure_warning_never_fires_outside_auto_mode(CookieSecureMode mode) =>
        Assert.False(SessionRules.CookieSecureBlockedByUntranslatedTls(mode, "http", "https://weir.example", null, hasTrustedProxy: false));

    [Fact]
    public void Session_validity_checks_revocation_absolute_expiry_then_idle()
    {
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var row = Session(now, absolute: now.AddHours(1), lastSeen: now);
        Assert.Null(SessionRules.InvalidReason(row, TimeSpan.FromHours(12), now));
        Assert.Equal(SessionInvalidReason.Revoked, SessionRules.InvalidReason(row with { RevokedAt = Timestamp.FromUtc(now) }, TimeSpan.FromHours(12), now));
        Assert.Equal(SessionInvalidReason.AbsoluteExpired, SessionRules.InvalidReason(Session(now, now.AddSeconds(-1), now.AddHours(-24)), TimeSpan.FromHours(12), now));
        Assert.Equal(SessionInvalidReason.AbsoluteExpired, SessionRules.InvalidReason(Session(now, now, now), TimeSpan.FromHours(12), now));
        Assert.Equal(SessionInvalidReason.IdleExpired, SessionRules.InvalidReason(Session(now, now.AddDays(7), now.AddHours(-13)), TimeSpan.FromHours(12), now));
        Assert.Null(SessionRules.InvalidReason(Session(now, now.AddDays(7), now.AddHours(-12)), TimeSpan.FromHours(12), now));
    }

    [Fact]
    public void Last_seen_is_touched_at_most_once_a_minute_or_half_the_idle_window() =>
        Assert.Equal(
            [TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)],
            new[] { SessionRules.LastSeenTouchGap(TimeSpan.FromHours(12)), SessionRules.LastSeenTouchGap(TimeSpan.FromMinutes(1)) });

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) Chrome/120.0", "Chrome on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Mac OS X) Safari/605", "Safari on macOS")]
    [InlineData("Mozilla/5.0 (X11; Linux) Firefox/120", "Firefox on Linux")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) Chrome/120 Edg/120", "Edge on Windows")]
    [InlineData("Weir Electron/30", "Weir app on device")]
    [InlineData(null, "Browser on device")]
    public void Session_labels_are_coarse(string? userAgent, string expected) =>
        Assert.Equal(expected, SessionRules.ClientLabelFromUserAgent(userAgent));

    [Fact]
    public void Password_strength_rules_and_messages_match()
    {
        Assert.Equal("Password must be at least 8 characters.", Core.Security.PasswordPolicy.Validate("short", "owner"));
        Assert.Equal("Password is too common. Choose a stronger password.", Core.Security.PasswordPolicy.Validate("password1234", "owner"));
        Assert.Equal("Password must not contain the username.", Core.Security.PasswordPolicy.Validate("xxOWNERyy12", "owner"));
        Assert.Equal("Password must use a wider mix of characters.", Core.Security.PasswordPolicy.Validate("aaabbbaaab", "owner"));
        Assert.Null(Core.Security.PasswordPolicy.Validate("longer-password-1", "owner"));
    }

    /// <summary>
    /// #556: the one minimum length every doc (README, docs/, docs-site/) and web hint (setup/bootstrap,
    /// settings change-password) must state. Pinned as a boundary, not just via the message text above, so a change
    /// to the constant is caught even if a future edit also updates the message string to match.
    /// </summary>
    [Fact]
    public void Minimum_password_length_is_pinned_at_8()
    {
        Assert.Equal(8, Core.Security.PasswordPolicy.MinPasswordLength);
        Assert.NotNull(Core.Security.PasswordPolicy.Validate("2345678", "owner"));
        Assert.Null(Core.Security.PasswordPolicy.Validate("23456789", "owner"));
    }

    [Fact]
    public void Rate_limiter_evicts_expired_buckets_and_caps_keys()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SlidingWindowLimiter(2, 10, clock);
        Assert.True(limiter.Allow("one"));
        Assert.True(limiter.Allow("two"));
        Assert.Equal(2, limiter.KeyCount);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(limiter.Allow("three"));
        Assert.Equal(1, limiter.KeyCount);

        var capped = new SlidingWindowLimiter(5, 60, clock, maxKeys: 2);
        Assert.True(capped.Allow("one"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(capped.Allow("two"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(capped.Allow("three"));
        Assert.Equal(2, capped.KeyCount);
        Assert.False(capped.HasKey("one"));
        Assert.True(capped.HasKey("three"));

        var limited = new SlidingWindowLimiter(1, 60, clock);
        Assert.True(limited.Allow(null));
        Assert.False(limited.Allow(string.Empty));
    }

    [Fact]
    public void Forwarded_for_is_used_only_through_a_trusted_proxy()
    {
        var warned = 0;
        Assert.Equal("172.18.0.2", ClientRateLimitKey.Resolve("172.18.0.2", "203.0.113.10", [], () => warned++));
        Assert.Equal(1, warned);
        Assert.Equal("172.18.0.2", ClientRateLimitKey.Resolve("172.18.0.2", "203.0.113.10", ["10.0.0.1"]));
        Assert.Equal("203.0.113.7", ClientRateLimitKey.Resolve("10.0.0.1", "198.51.100.20, 203.0.113.7, 10.0.0.1", ["10.0.0.0/24"]));
        Assert.Equal("unknown", ClientRateLimitKey.Resolve(null, null, []));
    }

    [Fact]
    public void Suite_settings_validation_messages_match()
    {
        var zones = new FakeZones();
        string Error(SuiteSettingsUpdate update) => Assert.Throws<WireValueException>(() => SuiteSettingsRules.Normalize(update, zones)).Message;
        Assert.Equal("Choose a valid timezone (for example: UTC, Europe/London, America/New_York).", Error(new("Not/A_Real_Zone", 30)));
        Assert.Equal("Log retention must be between 1 and 3650 days.", Error(new("UTC", 0)));
        Assert.Equal("Setup wizard state must be pending, skipped, or completed.", Error(new("UTC", 30, "later")));
        Assert.Equal("Backup time must use HH:MM in 24-hour time.", Error(new("UTC", 30, ConfigurationBackupPreferredTime: "ab:cd")));
        var normalized = SuiteSettingsRules.Normalize(new("UTC", 45, "COMPLETED", ConfigurationBackupPreferredTime: "3:5"), zones);
        Assert.Equal(("completed", "03:05"), (normalized.SetupWizardState, normalized.ConfigurationBackupPreferredTime));
        Assert.Equal("skipped", SuiteSettingsRules.DefaultSetupWizardState(1));
        Assert.Equal("pending", SuiteSettingsRules.DefaultSetupWizardState(0));
    }

    [Fact]
    public void Configuration_backup_waits_for_the_preferred_local_time_and_interval()
    {
        var zones = new FakeZones();
        var suite = new SuiteSettingsRecord
        {
            ConfigurationBackupEnabled = true,
            ConfigurationBackupIntervalHours = 24,
            ConfigurationBackupPreferredTime = "23:30",
            AppTimezone = "Australia/Sydney",
        };
        Assert.False(ConfigurationBackupSchedule.IsDue(suite, new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc), zones));
        Assert.True(ConfigurationBackupSchedule.IsDue(suite, new DateTime(2026, 4, 24, 13, 45, 0, DateTimeKind.Utc), zones));

        var hourly = suite with { ConfigurationBackupIntervalHours = 6, ConfigurationBackupLastRunAt = Timestamp.Naive(new DateTime(2026, 4, 24, 10, 0, 0)) };
        Assert.False(ConfigurationBackupSchedule.IsDue(hourly, new DateTime(2026, 4, 24, 15, 0, 0, DateTimeKind.Utc), zones));
        Assert.True(ConfigurationBackupSchedule.IsDue(hourly, new DateTime(2026, 4, 24, 16, 0, 0, DateTimeKind.Utc), zones));
        Assert.False(ConfigurationBackupSchedule.IsDue(suite with { ConfigurationBackupEnabled = false }, DateTime.UtcNow, zones));
    }

    [Fact]
    public void Pause_expiry_is_resolved_on_read()
    {
        var now = new DateTime(2026, 9, 17, 2, 0, 0, DateTimeKind.Utc);
        var until = Timestamp.Naive(new DateTime(2026, 9, 17, 4, 21, 1, 799).AddTicks(8780));
        var paused = PauseState.Resolve(new SuiteSettingsRecord { ProcessingPaused = true, ProcessingPausedUntil = until }, now);
        Assert.Equal(
            "{\"paused\":true,\"paused_until\":\"2026-09-17T04:21:01.799878Z\",\"scan_while_paused\":true,\"reason\":\"Processing is paused. Weir will start work again automatically at 2026-09-17 04:21 UTC.\",\"in_flight_policy\":\"Work already running finishes. Pausing stops Weir starting anything new.\"}",
            WireJsonWriter.Dumps(paused.ToOut(), WireJsonFormat.Response));
        Assert.True(PauseState.Resolve(new SuiteSettingsRecord { ProcessingPaused = true, ProcessingPausedUntil = until }, now.AddHours(3)).Expired);
        Assert.Equal("Processing is paused. Weir will start work again when you resume it.", PauseState.Resolve(new SuiteSettingsRecord { ProcessingPaused = true }, now).Reason);
    }

    [Fact]
    public void Security_overview_uses_plain_durations()
    {
        var overview = SecurityOverview.Build(Options);
        Assert.Equal("14 days", ((WireString)overview["standard_session_idle_timeout_plain"]).Value);
        Assert.Equal("1 minute", ((WireString)overview["sign_in_attempt_window_plain"]).Value);
        Assert.Equal("1 hour", ((WireString)overview["first_time_setup_attempt_window_plain"]).Value);
        Assert.Equal(["1 second", "59 seconds", "2 minutes", "1 day"], new[] { 1L, 59, 120, 86400 }.Select(SecurityOverview.PlainDuration));
    }

    [Fact]
    public void Failure_messages_classify_by_class_name_and_text()
    {
        var credentials = FailureMessages.FromException("Processing", "connection test", new FailureSubject("RuntimeError", "api_key=secret was rejected", ExceptionCategory.Other), provider: "sonarr");
        Assert.Equal(FailureKind.Credential, credentials.Kind);
        Assert.Contains("Processing connection test for Sonarr failed", credentials.Message, StringComparison.Ordinal);
        Assert.Contains("Re-enter the Sonarr credentials", credentials.NextAction, StringComparison.Ordinal);
        Assert.Contains("api_key=[redacted]", credentials.TechnicalDetail, StringComparison.Ordinal);

        var rate = FailureMessages.FromException("Processing", "subtitle search", new FailureSubject("RuntimeError", "HTTP 429 rate limit", ExceptionCategory.Other), provider: "opensubtitles_com", recoverable: true);
        Assert.Equal(FailureKind.RateLimit, rate.Kind);
        Assert.Contains("skipped and continued", rate.Message, StringComparison.Ordinal);
        Assert.Contains("continue with the next available provider", rate.WhatHappensNext, StringComparison.Ordinal);

        Assert.Equal(FailureKind.Filesystem, FailureMessages.Classify(FailureMessages.FromDotNet(new FileNotFoundException("D:/Downloads/film.mkv"))));
        Assert.Equal(FailureKind.Filesystem, FailureMessages.Classify(FailureMessages.FromDotNet(new UnauthorizedAccessException("denied"))));
        var gone = FailureMessages.FromException("Processing", "remux", FailureMessages.FromDotNet(new FileNotFoundException("gone")));
        Assert.Contains("file or folder", gone.Message + gone.NextAction, StringComparison.Ordinal);
        Assert.Equal(FailureKind.Network, FailureMessages.Classify(new FailureSubject("ConnectionRefusedError", "refused", ExceptionCategory.NetworkOrOs)));
        Assert.Equal(FailureKind.Validation, FailureMessages.Classify(new FailureSubject("ValueError", "bad", ExceptionCategory.Validation)));
        Assert.Equal(FailureKind.NotFound, FailureMessages.Classify(new FailureSubject("RuntimeError", "HTTP 404", ExceptionCategory.Other)));
        Assert.Equal(FailureKind.Auth, FailureMessages.Classify(new FailureSubject("RuntimeError", "403 Forbidden", ExceptionCategory.Other)));
        Assert.Equal(FailureKind.Internal, FailureMessages.Classify(new FailureSubject("RuntimeError", "boom", ExceptionCategory.Other)));

        // .NET exceptions map to the class names stored in technical details, and that name is part of what is searched.
        Assert.Equal(FailureKind.Validation, FailureMessages.Classify(FailureMessages.FromDotNet(new FormatException("bad payload"))));
        Assert.Equal(FailureKind.Network, FailureMessages.Classify(FailureMessages.FromDotNet(new IOException("pipe broke"))));
        Assert.Equal(FailureKind.NotFound, FailureMessages.Classify(FailureMessages.FromDotNet(new InvalidOperationException("file not found"))));
        Assert.Equal(FailureKind.Internal, FailureMessages.Classify(FailureMessages.FromDotNet(new InvalidOperationException("boom"))));
        Assert.Equal(FailureKind.RateLimit, FailureMessages.Classify(FailureMessages.RuntimeError("HTTP 429")));
        Assert.Equal("PermissionError: denied", FailureMessages.FromException("Processing", "job", FailureMessages.FromDotNet(new UnauthorizedAccessException("denied"))).TechnicalDetail);

        // #540 item 7: a blank provider is not named, and the fallback says "the" once, not
        // "Re-enter the the provider credentials...".
        var blank = FailureMessages.FromException("Processing", "sync", new FailureSubject("RuntimeError", "401", ExceptionCategory.Other), provider: "   ");
        Assert.Equal(("Processing sync failed: The service from  rejected the credentials or permission level. This job is marked failed so it does not look successful.", "Re-enter the provider credentials and run the connection test again."), (blank.Message, blank.NextAction));
    }

    [Fact]
    public void Diagnostics_and_operator_messages_keep_the_shared_shape()
    {
        var safe = new DiagnosticEvent("processing", "preview", "scheduled", "failed", Diagnostics.SeverityForResult("failed"), "Sonarr", "movies", "job-123",
            "Provider returned api_key=abc123 as rejected", "Re-enter the Sonarr API key and run the connection test again.",
            [new("scanned", 4), new("failed", 1)]).AsSafeDict();
        Assert.Equal(
            "{\"module\":\"processing\",\"action\":\"preview\",\"trigger\":\"scheduled\",\"result\":\"failed\",\"severity\":\"error\",\"provider\":\"Sonarr\",\"media_scope\":\"movies\",\"correlation_id\":\"job-123\",\"reason\":\"Provider returned api_key=[redacted] as rejected\",\"next_action\":\"Re-enter the Sonarr API key and run the connection test again.\",\"counts\":{\"scanned\":4,\"failed\":1}}",
            WireJsonWriter.Dumps(safe, WireJsonFormat.Response));
        Assert.Equal(["info", "info", "warning", "error"], new[] { "success", "skipped", "retrying", "failed" }.Select(Diagnostics.SeverityForResult));
        Assert.Equal(
            "{\"module\":\"processing\",\"action\":\"search\",\"trigger\":\"worker\",\"result\":\"skipped\",\"severity\":\"info\",\"provider\":\"opensubtitles\",\"media_scope_label\":\"Movies\",\"media_scope\":\"movies\",\"counts\":{\"checked\":1,\"downloaded\":0},\"user_message\":\"No subtitle was found.\"}",
            WireJsonWriter.Dumps(
                OperatorMessages.ActivityDetailEnvelope("processing", "search", "worker", "skipped", "opensubtitles", "movies", [new("checked", 1), new("downloaded", 0), new("bad_flag", null)], "No subtitle was found."),
                WireJsonFormat.Response));
        Assert.Equal("{\"failed\":0,\"removed\":3}", WireJsonWriter.Dumps(OperatorMessages.CountSummary([new("failed", -5), new("removed", 3)]), WireJsonFormat.Response));
        Assert.Equal(("Deluno", "TV episodes"), (OperatorMessages.ProviderLabel("deluno"), OperatorMessages.MediaScopeLabel("tv")));
        Assert.Equal(5, MetricsTruth.FinalizedSuccessTotal(new Dictionary<string, long> { ["output_written"] = 2, ["unchanged_copied"] = 3 }));
        Assert.Contains("must not be negative", Assert.Throws<WireValueException>(() => MetricsTruth.RequireNonNegative(new Dictionary<string, long> { ["files_processed"] = -1 })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_metrics_render_as_prometheus_text_and_a_summary()
    {
        var store = new RuntimeMetricsStore(new FakeTimeProvider());
        store.RecordRequest("GET", "/api/v1/health", 200, 12.5);
        store.RecordRequest("POST", "/api/v1/processing/jobs", 500, 33.0);
        store.RecordLog("error");
        store.RecordModuleJobEvent("processing", "started");
        store.RecordModuleJobEvent("processing", "completed");
        store.RecordModuleJobEvent("other", "failed");
        store.SetModuleQueueDepth("processing", 3);
        store.SetModuleQueueDepth("other", 1);
        var output = store.RenderPrometheus();
        Assert.Contains("weir_module_jobs_total{module=\"processing\",event=\"started\"} 1", output, StringComparison.Ordinal);
        Assert.Contains("weir_module_jobs_total{module=\"other\",event=\"failed\"} 1", output, StringComparison.Ordinal);
        Assert.Contains("weir_module_queue_depth{module=\"other\"} 1", output, StringComparison.Ordinal);
        Assert.Contains("weir_http_requests_total 2", output, StringComparison.Ordinal);
        Assert.Contains("weir_log_records_total{level=\"error\"} 1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("weir_module_savings_bytes_total", output, StringComparison.Ordinal);

        store.RecordModuleSavings("processing", 12_345_678);
        store.RecordModuleSavings("processing", 0);
        store.RecordModuleSavings("other", 9_876_543);
        output = store.RenderPrometheus();
        Assert.Contains("# TYPE weir_module_savings_bytes_total counter", output, StringComparison.Ordinal);
        Assert.Contains("weir_module_savings_bytes_total{module=\"processing\"} 12345678", output, StringComparison.Ordinal);
        var summary = WireJsonWriter.Dumps(store.SuiteMetricsOut(), WireJsonFormat.Response);
        Assert.Contains("\"error_log_count\":1,\"status_counts\":{\"2xx\":1,\"3xx\":0,\"4xx\":0,\"5xx\":1}", summary, StringComparison.Ordinal);
        Assert.Contains("{\"route\":\"GET /api/v1/health\",\"request_count\":1,\"average_response_ms\":12.5}", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_reading_returns_the_newest_matching_rows_and_counts()
    {
        var filter = new SuiteLogFilter(null, "row", null, 5);
        for (var i = 0; i < 20; i++)
        {
            filter.Add($"{{\"timestamp\":\"2026-05-09T10:00:{i:00}Z\",\"level\":\"INFO\",\"logger\":\"weir.tests\",\"message\":\"row-{i}\",\"source\":\"test.py:1\"}}");
        }

        filter.Add("not json");
        filter.Add("{\"timestamp\":\"2026-05-09T10:00:00Z\",\"level\":\"INFO\",\"logger\":\"uvicorn\",\"message\":\"noise\"}");
        var result = filter.Result();
        Assert.Equal(20, result.Total);
        Assert.Equal(20, result.Information);
        Assert.Equal(["row-19", "row-18", "row-17", "row-16", "row-15"], result.Items.Select(item => item.Message));
        Assert.Null(SuiteLogFilter.ToOut(result.Items[0] with { Timestamp = "not-a-time" }));
        Assert.Equal("2026-05-09T10:00:19Z", ((WireString)SuiteLogFilter.ToOut(result.Items[0])!["timestamp"]).Value);
    }

    [Theory]
    [InlineData("WARNING", "Some.Library", false)]
    [InlineData("ERROR", "Some.Library", false)]
    [InlineData("CRITICAL", "Some.Library", false)]
    [InlineData("INFO", "weir.platform.activity", false)]
    [InlineData("INFO", "Some.Library", true)]
    [InlineData("INFO", "Weir.Capitalised", true)]
    public void The_log_leaves_out_information_that_is_not_from_weirs_own_loggers(string level, string logger, bool noise)
    {
        Assert.Equal(noise, SuiteLogFilter.IsLowValueNoise(level, logger));
    }

    [Fact]
    public void Release_versions_and_update_status_are_reported_exactly()
    {
        Assert.Equal("2.0.8", ReleaseCatalog.NormalizeReleaseVersion("v2.0.8"));
        Assert.Equal("v2.0.8", ReleaseCatalog.TagForVersion("2.0.8"));
        GitHubReleaseAsset Asset(string name) => new(name, $"https://api.github.test/assets/{name}", $"https://downloads.github.test/{name}", 123, "application/octet-stream");
        var current = Asset("Weir-win-Setup.exe");
        var legacy = Asset("WeirSetup.exe");
        GitHubReleaseRecord Record(string version, params GitHubReleaseAsset[] assets) =>
            new($"v{version}", version, $"Weir {version}", "https://example.com/release", Timestamp.FromUtc(new DateTime(2026, 5, 7, 0, 0, 0, DateTimeKind.Utc)), false, false, assets);
        Assert.Same(current, Record("2.6.1", legacy, current).WindowsInstallerAsset());
        Assert.Same(legacy, Record("2.6.1", legacy).WindowsInstallerAsset());

        var windows = UpdateStatus.FromRelease("2.0.7", "windows", Record("2.0.8", current));
        Assert.Equal("update_available", ((WireString)windows["status"]).Value);
        Assert.Equal("Updates are managed by the Weir desktop app via Velopack.", ((WireString)windows["in_app_upgrade_summary"]).Value);
        Assert.Equal("2026-05-07T00:00:00Z", ((WireString)windows["published_at"]).Value);
        Assert.Equal("up_to_date", ((WireString)UpdateStatus.FromRelease("2.1.4", "windows", Record("2.1.4"))["status"]).Value);
        var docker = UpdateStatus.FromRelease("2.0.7", "docker", Record("2.0.8"));
        Assert.Equal("docker compose pull && docker compose up -d", ((WireString)docker["docker_update_command"]).Value);
        Assert.False(((WireBool)docker["in_app_upgrade_supported"]).Value);
        Assert.IsType<WireNull>(docker["in_app_upgrade_summary"]);

        Assert.Null(UpdateStatus.ParseUpdateSettings("{\"mode\": \"Notify"));
        Assert.Null(UpdateStatus.ParseUpdateSettings("{\"mode\": \"InstallEverythingNow\"}"));
        Assert.Equal("{\"mode\":\"DownloadOnly\",\"check_on_startup\":false,\"check_interval_minutes\":240,\"check_for_updates\":true}",
            WireJsonWriter.Dumps(UpdateStatus.ParseUpdateSettings("{\"mode\": \"DownloadOnly\", \"checkOnStartup\": false, \"checkIntervalMinutes\": 240}")!, WireJsonFormat.Response));
        Assert.Equal("{\n  \"mode\": \"Auto\",\n  \"checkOnStartup\": true,\n  \"checkIntervalMinutes\": 1,\n  \"checkForUpdates\": false\n}", UpdateStatus.SerializeUpdateSettings("Auto", true, 1, false));
    }

    [Fact]
    public void Notification_rules_and_url_policy_refuse_with_exact_messages()
    {
        string Error(string label, string provider, string url, params string[] events) =>
            Assert.Throws<WireValueException>(() => NotificationRules.Validate(label, provider, url, events)).Message;
        Assert.Equal("Unsupported provider: 'slack'. Choose from: webhook, discord", Error("x", "slack", "https://example.com", "job_failed"));
        Assert.Equal("Blocked provider URL host: 10.0.0.1", Error("x", "discord", "http://10.0.0.1/x"));
        Assert.Equal("Blocked provider URL host: localhost", Error("x", "discord", "http://LOCALHOST:80/x", "job_failed"));
        Assert.Equal("Blocked provider URL scheme: ftp", Error("x", "discord", "ftp://example.com/x", "job_failed"));
        Assert.Equal("Unknown events: nope. Supported: job_completed, job_failed, processing_job_completed, processing_job_failed", Error("x", "discord", "https://example.com/x", "nope"));
        Assert.Equal("At least one event must be selected.", Error("x", "discord", "https://example.com/x"));
        Assert.Equal("Label must not be empty.", Error("  ", "discord", "https://example.com/x", "job_failed"));
        Assert.Equal("Invalid IPv6 URL", Error("x", "discord", "https://[::1/x", "job_failed"));
        Assert.Equal("[\"job_failed\", \"processing_job_completed\"]", NotificationRules.SerializeEvents(["job_failed", "processing_job_completed"]));
        Assert.Equal(["job_failed"], NotificationRules.ParseEvents("[\"job_failed\", 5]"));
        Assert.Empty(NotificationRules.ParseEvents("{bad"));

        Assert.True(NetAddress.TryParse("203.0.113.7", out var documentation) && !documentation.IsGlobal);
        Assert.True(NetAddress.TryParse("100.64.0.1", out var shared) && !shared.IsGlobal && !shared.IsPrivate);
        Assert.True(NetAddress.TryParse("8.8.8.8", out var google) && google.IsGlobal);
        Assert.True(NetAddress.TryParse("::ffff:10.0.0.1", out var mapped) && mapped.IsPrivate);
        Assert.False(NetAddress.TryParse("01.1.1.1", out _));
        Assert.True(NetRange.TryParse("10.0.0.5/24", strict: false, out var network) && network.Contains(documentation) is false);
        Assert.False(NetRange.TryParse("10.0.0.5/24", strict: true, out _));
    }

    [Fact]
    public void Job_notification_wording_is_retry_aware()
    {
        // #540 item 6: a failed attempt that is about to retry says a retry is coming rather than
        // "exhausted all retry attempts", using the same #488 vocabulary WorkerFailures uses for the
        // stored job error.
        Assert.Equal(
            ("processing_job_completed", "Weir on RIG job completed", "Job 5 (processing.test.v1) finished successfully."),
            NotificationRules.JobNotification("processing", "completed", 5, "processing.test.v1", "Weir on RIG"));
        Assert.Equal(
            ("processing_job_failed", "Weir on RIG job failed", "Job 5 (processing.test.v1) exhausted all retry attempts."),
            NotificationRules.JobNotification("processing", "failed", 5, "processing.test.v1", "Weir on RIG"));
        Assert.Equal(
            ("processing_job_failed", "Weir on RIG job failed", "Job 5 (processing.test.v1) failed. Weir will try this job again shortly."),
            NotificationRules.JobNotification("processing", "failed", 5, "processing.test.v1", "Weir on RIG", willRetry: true));
    }

    private static UserSessionRecord Session(DateTime now, DateTime absolute, DateTime lastSeen) =>
        new("ab", 1, "cd", Timestamp.FromUtc(now), Timestamp.FromUtc(absolute), false, Timestamp.FromUtc(lastSeen), null, "Browser session");

    private sealed class FakeZones : ITimeZoneResolver
    {
        public bool TryFind(string name, out TimeZoneInfo zone)
        {
            zone = TimeZoneInfo.Utc;
            switch (name)
            {
                case "UTC":
                    return true;
                case "Australia/Sydney":
                    // AEST, no daylight saving in April after the first Sunday.
                    zone = TimeZoneInfo.CreateCustomTimeZone("Australia/Sydney", TimeSpan.FromHours(10), "Sydney", "AEST");
                    return true;
                default:
                    return false;
            }
        }
    }
}
