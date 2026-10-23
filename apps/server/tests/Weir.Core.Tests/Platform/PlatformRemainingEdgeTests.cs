using System.Text;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Readiness;
using Weir.Core.Rules;
using Weir.Core.Security;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Core.Updates;
using Weir.Core.Validation;
using Weir.Core.Workers;

namespace Weir.Core.Tests.Platform;

/// <summary>The last edges of settings, secrets, readiness, updates, times and the processing pass: values at their limits and fields that are missing.</summary>
public sealed class PlatformRemainingEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static WireValue Json(string json) => WireJsonParser.Parse(json);

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1L, true)]
    [InlineData(720L, true)]
    [InlineData(721L, false)]
    [InlineData(null, true)]
    public void The_backup_interval_is_one_to_seven_hundred_and_twenty_hours(long? hours, bool accepted)
    {
        var update = new SuiteSettingsUpdate("UTC", 30, ConfigurationBackupIntervalHours: hours);

        if (accepted)
        {
            Assert.Equal(hours, SuiteSettingsRules.Normalize(update, new IanaTimeZoneResolver()).ConfigurationBackupIntervalHours);
        }
        else
        {
            Assert.Throws<WireValueException>(() => SuiteSettingsRules.Normalize(update, new IanaTimeZoneResolver()));
        }
    }

    [Theory]
    [InlineData("  ", "UTC", 24)]
    [InlineData("Europe/London", "Europe/London", 6)]
    public void Settings_go_out_with_the_timezone_and_interval_defaults_filled_in(string timezone, string expected, long expectedInterval)
    {
        var row = new SuiteSettingsRecord { AppTimezone = timezone, ConfigurationBackupIntervalHours = expectedInterval == 24 ? 0 : expectedInterval };

        var wire = SuiteSettingsRules.BuildOut(row);

        Assert.Equal(expected, ((WireString)wire.Get("app_timezone")!).Value);
        Assert.Equal(expectedInterval, ((WireInteger)wire.Get("configuration_backup_interval_hours")!).Value);
    }

    [Fact]
    public void A_session_length_too_long_to_count_in_seconds_is_shown_at_the_longest()
    {
        var options = TestRuntime.Load() with { SessionIdleMinutes = long.MaxValue };

        var overview = SecurityOverview.Build(options);

        Assert.Equal(SecurityOverview.PlainDuration(long.MaxValue), ((WireString)overview.Get("standard_session_idle_timeout_plain")!).Value);
    }

    [Fact]
    public void A_backup_interval_too_long_to_count_in_seconds_is_held_to_thirty_days_and_an_empty_timezone_is_utc()
    {
        var suite = new SuiteSettingsRecord
        {
            ConfigurationBackupEnabled = true,
            ConfigurationBackupIntervalHours = long.MaxValue,
            AppTimezone = " ",
            ConfigurationBackupLastRunAt = Timestamp.FromDateTimeOffset(Now.AddDays(-29)),
        };

        Assert.False(ConfigurationBackupSchedule.IsDue(suite, Now.UtcDateTime, new IanaTimeZoneResolver()));
        Assert.True(ConfigurationBackupSchedule.IsDue(suite with { ConfigurationBackupLastRunAt = Timestamp.FromDateTimeOffset(Now.AddDays(-31)) }, Now.UtcDateTime, new IanaTimeZoneResolver()));
    }

    [Theory]
    [InlineData("0000-01-01T00:00:00")]
    [InlineData("2026-00-10T00:00:00")]
    [InlineData("2026-13-10T00:00:00")]
    [InlineData("2026-01-00T00:00:00")]
    [InlineData("2026-02-30T00:00:00")]
    [InlineData("2026-01-01T24:00:00")]
    [InlineData("2026-01-01T00:60:00")]
    [InlineData("2026-01-01T00:00:60")]
    [InlineData("2026-01-01T1")]
    [InlineData("2026-01-01T10:3")]
    public void A_date_or_time_that_does_not_exist_is_not_a_timestamp(string text)
    {
        Assert.False(Timestamp.TryFromIsoFormat(text, out _));
    }

    [Theory]
    [InlineData("""{"tag_name": "v1.0.0", "assets": [{"name": "a.exe"}]}""")]
    [InlineData("""{"tag_name": "v1.0.0", "assets": [{"url": "https://x/a"}]}""")]
    [InlineData("""{"tag_name": "v1.0.0", "assets": [{"name": "", "url": "https://x/a"}]}""")]
    public void A_release_asset_without_a_name_or_an_address_makes_the_release_unusable(string release)
    {
        Assert.Throws<WireValueException>(() => ReleaseCatalog.CoerceReleasePayload(Json(release)));
    }

    [Fact]
    public void A_release_known_before_the_rate_limit_keeps_saying_whether_it_was_an_update()
    {
        var older = new GitHubReleaseRecord("v0.0.1", "0.0.1", null, null, null, false, false, []);
        var newer = new GitHubReleaseRecord("v99.0.0", "99.0.0", null, null, null, false, false, []);

        var withNothing = UpdateStatus.RateLimited("1.0.0", "windows", null, Now, "Slow down.");
        var withOlder = UpdateStatus.RateLimited("1.0.0", "windows", older, Now, "Slow down.");
        var withNewer = UpdateStatus.RateLimited("1.0.0", "windows", newer, Now, "Slow down.");

        Assert.False(withNothing.Get("known_update_available")!.IsTruthy);
        Assert.False(withOlder.Get("known_update_available")!.IsTruthy);
        Assert.True(withNewer.Get("known_update_available")!.IsTruthy);
    }

    [Theory]
    [InlineData("""{"pending_version": "2.0.0", "last_checked_at": "2026-10-02T12:00:00Z"}""", "2.0.0")]
    [InlineData("""{"pending_version": "", "last_checked_at": 5}""", null)]
    [InlineData("""{}""", null)]
    public void A_step_keeps_the_pending_version_only_when_there_is_one(string state, string? pending)
    {
        var stepped = UpdateStatus.WithStep((WireObject)Json(state), "downloading");

        Assert.Equal(pending, (stepped.Get("pending_version") as WireString)?.Value);
    }

    [Fact]
    public void A_feed_entry_with_no_title_or_a_bad_updated_time_is_still_a_release()
    {
        const string Feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link href="https://example/releases/tag/v1.0.1"/></entry>
              <entry><link href="https://example/releases/tag/v1.0.2"/><title>  </title><updated>   </updated></entry>
              <entry><link href="https://example/releases/tag/v1.0.3"/><title>Weir 1.0.3</title><updated>not a time</updated></entry>
            </feed>
            """;

        var releases = ReleaseFeed.Parse(Feed);

        Assert.Equal(3, releases.Count);
        Assert.All(releases, release => Assert.Null(release.PublishedAt));
        Assert.Null(releases[0].ReleaseName);
        Assert.Null(releases[1].ReleaseName);
        Assert.Equal("Weir 1.0.3", releases[2].ReleaseName);
    }

    [Theory]
    [InlineData("""{"v": "b"}""", true)]
    [InlineData("""{"v": "c"}""", false)]
    [InlineData("""{"v": 5}""", false)]
    public void A_literal_must_be_one_of_the_allowed_words(string input, bool accepted)
    {
        var issues = new ValidationIssues();
        var value = ((WireObject)Json(input)).Get("v")!;

        Assert.Equal(accepted, FieldRules.TryLiteral(value, ["v"], ["a", "b"], issues, out _));
    }

    [Fact]
    public void A_version_is_always_read_from_the_build()
    {
        Assert.False(string.IsNullOrWhiteSpace(WeirVersion.BuildVersion));
    }

    [Fact]
    public void A_whole_number_in_a_backup_that_does_not_fit_in_an_int_cannot_be_read()
    {
        foreach (var number in new[] { "3000000000", "-3000000000" })
        {
            var section = Json($$"""{"format": 1, "kdf": "argon2id", "memory_kib": {{number}}, "iterations": 3, "parallelism": 1, "salt": "", "nonce": "", "tag": "", "ciphertext": ""}""");

            Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(section, "password"));
        }
    }

    [Fact]
    public void An_envelope_with_no_key_id_is_opened_with_the_session_secret()
    {
        var cipher = new CredentialCipher(null, "session-secret", [], TimeProvider.System);
        var token = CredentialCipher.ForSecret("session-secret")!.Encrypt(Encoding.UTF8.GetBytes("arr-api-key"), TimeProvider.System);
        var envelope = WireJsonWriter.Dumps(new WireObject().Set("version", 2L).Set("token", token), WireJsonFormat.Compact);

        Assert.Equal("arr-api-key", cipher.Decrypt(envelope));
    }

    private static WorkerLaneHealth Lane(string status) => new("processing", 1, 1, 0, 0, status, "detail");

    [Theory]
    [InlineData(true, true, "healthy", true)]
    [InlineData(true, true, "disabled", true)]
    [InlineData(true, true, "stale", false)]
    [InlineData(true, false, "healthy", false)]
    [InlineData(false, true, "healthy", false)]
    public void Workers_are_ready_once_started_recovered_and_all_healthy_or_off(bool started, bool recovered, string status, bool ready)
    {
        var report = ReadinessBuilder.Build(new ReadinessInputs(TimeSpan.Zero, true, started, [Lane(status)], ReadinessBuilder.NoWatchedLibraries, recovered), "1.0.0");

        Assert.Equal(ready, report.Steps.Single(step => step.Name == "workers").Status == "ready");
    }

    [Theory]
    [InlineData(true, "ready")]
    [InlineData(false, "starting")]
    public void Workers_that_are_not_reported_on_are_ready_with_startup(bool started, string status)
    {
        var report = ReadinessBuilder.Build(new ReadinessInputs(TimeSpan.Zero, true, started, null, ReadinessBuilder.NoWatchedLibraries), "1.0.0");

        Assert.Equal(status, report.Steps.Single(step => step.Name == "workers").Status);
    }

    [Theory]
    [InlineData(0, "off")]
    [InlineData(1, "one available")]
    [InlineData(4, "4 in-process worker slots")]
    public void The_worker_summary_says_whether_workers_are_off_one_or_several(int workers, string expected)
    {
        var settings = RuntimeVisibility.From(TestRuntime.Load() with { ProcessingWorkerCount = workers });

        Assert.Contains(expected, settings.WorkerModeSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 4000L, "4k")]
    [InlineData(null, 300L, "sd")]
    [InlineData(null, 0L, "undetermined")]
    [InlineData(null, null, "undetermined")]
    [InlineData(3000L, null, "4k")]
    [InlineData(100L, 4000L, "sd")]
    public void A_file_is_classed_by_its_width_and_otherwise_its_height(long? width, long? height, string expected)
    {
        Assert.Equal(expected, RunnerUnits.ResolutionClassForDimensions(width, height));
    }

    [Theory]
    [InlineData(null, "keep_all")]
    [InlineData("REMOVE_ALL", "remove_all")]
    [InlineData("keep_listed", "keep_selected")]
    [InlineData("anything", "keep_all")]
    public void A_subtitle_mode_that_is_missing_or_unknown_keeps_every_subtitle(string? raw, string expected)
    {
        Assert.Equal(expected, RuleSetConversion.NormalizeSubtitleMode(raw));
    }

    [Fact]
    public void A_rule_set_with_missing_lists_gives_empty_ones()
    {
        var config = RuleSetConversion.ToRulesConfig(new ProcessingRuleSetRecord { Name = "x", SubtitleLangsCsv = null!, AudioSortersJson = null!, SubtitleSortersJson = null! });

        Assert.Empty(config.SubtitleLangs);
        Assert.Equal(string.Empty, config.AudioSortersJson);
        Assert.Equal(string.Empty, config.SubtitleSortersJson);
    }

    [Theory]
    [InlineData(true, true, "ok")]
    [InlineData(true, false, "ok")]
    [InlineData(false, true, "ok")]
    public void A_retry_is_decided_by_the_failure_class_and_the_workflows_policy(bool retryPreflight, bool retryExecution, string unused)
    {
        Assert.NotEmpty(unused);
        var library = new ProcessingLibraryRecord { Name = "Movies", RetryPreflightFailures = retryPreflight, RetryExecutionFailures = retryExecution, MaxAttempts = 3, RetryBackoffSeconds = 30 };

        var guardrail = RetryPolicy.DecideRetry(library, ProcessingFailureClasses.Guardrail, 1, Now);
        var execution = RetryPolicy.DecideRetry(library, ProcessingFailureClasses.Execution, 1, Now);

        Assert.False(guardrail.WillRetry);
        Assert.Contains("safety check", guardrail.Reason, StringComparison.Ordinal);
        Assert.Equal(retryExecution, execution.WillRetry);
        Assert.Contains(retryExecution ? "about 1 minute" : "set not to retry", execution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_of_a_kind_that_is_not_known_is_not_retried_and_says_so()
    {
        var decision = RetryPolicy.DecideRetry(new ProcessingLibraryRecord { Name = "Movies" }, "mystery", 1, Now);

        Assert.False(decision.WillRetry);
        Assert.Contains("could not work out why", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_probe_that_is_not_an_object_has_no_duration_dimensions_or_flags()
    {
        var probe = ProbeResult.Parse("[]");

        Assert.Null(RemuxPassMedia.ProbeDurationSeconds(probe));
    }

    [Theory]
    [InlineData("""{"width": 0, "coded_width": 1920}""", 1920L)]
    [InlineData("""{"width": 0}""", null)]
    public void A_stream_width_of_zero_falls_back_to_the_next_name(string stream, long? width)
    {
        Assert.Equal(width, RemuxPassMedia.VideoDimensions([ProbeStreamInfo.Parse(stream)]).Width);
    }

    [Theory]
    [InlineData("""{"duration": true}""", 1.0)]
    [InlineData("""{"duration": false}""", null)]
    [InlineData("""{"duration": [1]}""", null)]
    public void A_duration_written_as_a_boolean_or_a_list_is_read_as_the_pass_reads_it(string stream, double? seconds)
    {
        var probe = ProbeResult.Parse("{\"streams\": [" + stream + "]}");

        Assert.Equal(seconds, RemuxPassMedia.ProbeDurationSeconds(probe));
    }

    [Theory]
    [InlineData("""{"bits_per_raw_sample": true}""")]
    [InlineData("""{"bits_per_raw_sample": []}""")]
    [InlineData("""{"disposition": 3}""")]
    public void A_bit_depth_or_flag_that_is_not_the_expected_kind_is_ignored(string stream)
    {
        var info = ProbeStreamInfo.Parse(stream);

        Assert.Null(RemuxPassMedia.VideoBitDepth(info));
    }
}
