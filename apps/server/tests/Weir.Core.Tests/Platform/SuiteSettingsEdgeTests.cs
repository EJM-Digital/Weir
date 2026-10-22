using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Settings;
using Weir.Core.Time;

namespace Weir.Core.Tests.Platform;

/// <summary>The refusals, fall-backs and plain-language wording of the suite settings that the ordinary round trip does not reach.</summary>
public sealed class SuiteSettingsEdgeTests
{
    private static readonly ITimeZoneResolver Zones = new IanaTimeZoneResolver();

    private static string Text(WireObject o, string key) => ((WireString)o[key]).Value;

    private static string Refusal(SuiteSettingsUpdate update) =>
        Assert.Throws<WireValueException>(() => SuiteSettingsRules.Normalize(update, Zones)).Message;

    [Fact]
    public void A_missing_or_blank_timezone_is_refused_before_it_is_looked_up()
    {
        Assert.Equal("Timezone cannot be empty.", Refusal(new SuiteSettingsUpdate(null!, 30)));
        Assert.Equal("Timezone cannot be empty.", Refusal(new SuiteSettingsUpdate("   ", 30)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    public void Activity_history_outside_zero_to_3650_days_is_refused(long days)
    {
        Assert.Equal(
            "Activity history must be kept between 0 (forever) and 3650 days.",
            Refusal(new SuiteSettingsUpdate("UTC", 30, ActivityRetentionDays: days)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3650)]
    public void Activity_history_at_the_ends_of_its_range_is_kept(long days)
    {
        var normalized = SuiteSettingsRules.Normalize(new SuiteSettingsUpdate("UTC", 30, ActivityRetentionDays: days), Zones);

        Assert.Equal(days, normalized.ActivityRetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(721)]
    public void A_backup_interval_outside_1_to_720_hours_is_refused(long hours)
    {
        Assert.Equal(
            "Backup interval must be between 1 and 720 hours.",
            Refusal(new SuiteSettingsUpdate("UTC", 30, ConfigurationBackupIntervalHours: hours)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_blank_backup_time_means_two_in_the_morning(string? raw)
    {
        Assert.True(SuiteSettingsRules.TryParseTime(raw, out var hour, out var minute));
        Assert.Equal((2, 0), (hour, minute));
        Assert.Equal("02:00", SuiteSettingsRules.NormalizeBackupPreferredTime(raw));
    }

    [Fact]
    public void Settings_read_back_from_a_damaged_row_are_shown_as_the_defaults()
    {
        var shown = SuiteSettingsRules.BuildOut(new SuiteSettingsRecord
        {
            SetupWizardState = null!,
            AppTimezone = null!,
            ConfigurationBackupIntervalHours = 0,
        });
        Assert.Equal("pending", Text(shown, "setup_wizard_state"));
        Assert.Equal("UTC", Text(shown, "app_timezone"));
        Assert.Equal(24, ((WireInteger)shown["configuration_backup_interval_hours"]).Value);

        var odd = SuiteSettingsRules.BuildOut(new SuiteSettingsRecord { SetupWizardState = "  ", AppTimezone = "  " });
        Assert.Equal("pending", Text(odd, "setup_wizard_state"));
        Assert.Equal("UTC", Text(odd, "app_timezone"));

        Assert.Equal("pending", Text(SuiteSettingsRules.BuildOut(new SuiteSettingsRecord { SetupWizardState = "finished" }), "setup_wizard_state"));
        Assert.Equal("completed", Text(SuiteSettingsRules.BuildOut(new SuiteSettingsRecord { SetupWizardState = " Completed " }), "setup_wizard_state"));
    }

    [Fact]
    public void The_security_overview_says_a_missing_signing_secret_is_not_configured()
    {
        var options = TestRuntime.Load();

        Assert.True(((WireBool)SecurityOverview.Build(options with { SessionSecret = "s3cret" })["session_signing_configured"]).Value);
        Assert.False(((WireBool)SecurityOverview.Build(options with { SessionSecret = "  " })["session_signing_configured"]).Value);
        Assert.False(((WireBool)SecurityOverview.Build(options with { SessionSecret = null })["session_signing_configured"]).Value);
    }

    [Theory]
    [InlineData(CookieSameSite.Strict, "Strict (tighter; can break some flows)")]
    [InlineData(CookieSameSite.None, "None (advanced; only makes sense with HTTPS)")]
    [InlineData(CookieSameSite.Lax, "Lax (recommended for most setups)")]
    public void The_security_overview_explains_the_same_site_choice(CookieSameSite sameSite, string expected)
    {
        var shown = SecurityOverview.Build(TestRuntime.Load() with { SessionCookieSameSite = sameSite });

        Assert.Equal(expected, Text(shown, "sign_in_cookie_same_site"));
    }

    [Theory]
    [InlineData(CookieSecureMode.Always, "Always on. Sign-in will not work unless the app is reached over HTTPS.")]
    [InlineData(CookieSecureMode.Never, "Always off. The sign-in cookie is sent over plain HTTP as well as HTTPS.")]
    [InlineData(CookieSecureMode.Auto, "Matched to each connection — on over HTTPS, off over plain HTTP on your network.")]
    public void The_security_overview_explains_the_https_cookie_choice(CookieSecureMode mode, string expected)
    {
        var shown = SecurityOverview.Build(TestRuntime.Load() with { SessionCookieSecureMode = mode });

        Assert.Equal(expected, Text(shown, "sign_in_cookie_https_plain"));
    }

    [Fact]
    public void A_timeout_too_large_to_count_in_seconds_is_shown_as_the_largest_it_can_be()
    {
        var shown = SecurityOverview.Build(TestRuntime.Load() with { SessionIdleMinutes = int.MaxValue, SessionAbsoluteDays = int.MaxValue });

        Assert.EndsWith(" days", Text(shown, "standard_session_idle_timeout_plain"));
        Assert.EndsWith(" days", Text(shown, "standard_session_absolute_timeout_plain"));
        Assert.Equal("1 day", SecurityOverview.PlainDuration(86400));
    }

    [Fact]
    public void A_backup_that_ran_earlier_the_same_local_day_is_not_due_again_even_after_a_full_day_has_passed()
    {
        // The clocks go back on 1 November 2026, so that local day is 25 hours long: 24 hours after a backup at 00:30 it is still the same day.
        var suite = new SuiteSettingsRecord
        {
            ConfigurationBackupEnabled = true,
            ConfigurationBackupIntervalHours = 24,
            ConfigurationBackupPreferredTime = "00:15",
            AppTimezone = "America/New_York",
            ConfigurationBackupLastRunAt = Timestamp.FromUtc(new DateTime(2026, 11, 1, 4, 30, 0, DateTimeKind.Utc)),
        };

        Assert.False(ConfigurationBackupSchedule.IsDue(suite, new DateTime(2026, 11, 2, 4, 30, 0, DateTimeKind.Utc), Zones));
        Assert.True(ConfigurationBackupSchedule.IsDue(suite, new DateTime(2026, 11, 2, 5, 30, 0, DateTimeKind.Utc), Zones));
    }

    [Fact]
    public void A_backup_that_ran_an_hour_ago_is_not_due_again_until_its_hourly_interval_has_passed()
    {
        var suite = new SuiteSettingsRecord
        {
            ConfigurationBackupEnabled = true,
            ConfigurationBackupIntervalHours = 2,
            ConfigurationBackupPreferredTime = "01:00",
            AppTimezone = "UTC",
        };
        var now = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(ConfigurationBackupSchedule.IsDue(suite with { ConfigurationBackupLastRunAt = Timestamp.FromUtc(now.AddHours(-1)) }, now, Zones));
        Assert.True(ConfigurationBackupSchedule.IsDue(suite with { ConfigurationBackupLastRunAt = Timestamp.FromUtc(now.AddHours(-2)) }, now, Zones));
    }

    [Fact]
    public void A_backup_with_unusable_settings_falls_back_to_utc_at_two_in_the_morning_every_day()
    {
        var suite = new SuiteSettingsRecord
        {
            ConfigurationBackupEnabled = true,
            ConfigurationBackupIntervalHours = 0,
            ConfigurationBackupPreferredTime = "soon",
            AppTimezone = "  ",
        };

        Assert.False(ConfigurationBackupSchedule.IsDue(suite, new DateTime(2026, 4, 24, 1, 59, 0, DateTimeKind.Utc), Zones));
        Assert.True(ConfigurationBackupSchedule.IsDue(suite, new DateTime(2026, 4, 24, 2, 0, 0, DateTimeKind.Utc), Zones));

        var unknownZone = suite with { AppTimezone = "Not/AZone", ConfigurationBackupPreferredTime = "02:00" };
        Assert.True(ConfigurationBackupSchedule.IsDue(unknownZone, new DateTime(2026, 4, 24, 2, 0, 0, DateTimeKind.Utc), Zones));
    }

    [Fact]
    public void A_backup_interval_beyond_the_largest_count_of_seconds_waits_the_longest_it_can()
    {
        var suite = new SuiteSettingsRecord
        {
            ConfigurationBackupEnabled = true,
            ConfigurationBackupIntervalHours = long.MaxValue,
            ConfigurationBackupPreferredTime = "00:00",
            AppTimezone = "UTC",
            ConfigurationBackupLastRunAt = Timestamp.FromUtc(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)),
        };

        Assert.False(ConfigurationBackupSchedule.IsDue(suite, new DateTime(2026, 4, 25, 0, 0, 0, DateTimeKind.Utc), Zones));
        Assert.True(ConfigurationBackupSchedule.IsDue(suite, new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), Zones));
    }
}
