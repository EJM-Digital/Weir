using System.Globalization;
using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Time;

namespace Weir.Core.Settings;

/// <summary>The <c>suite_settings</c> singleton row (id = 1).</summary>
public sealed record SuiteSettingsRecord
{
    public string SetupWizardState { get; init; } = "pending";
    public string AppTimezone { get; init; } = "UTC";
    public long LogRetentionDays { get; init; } = 30;
    public long ActivityRetentionDays { get; init; } = 90;
    public string DirectPlayDevices { get; init; } = string.Empty;
    public bool ConfigurationBackupEnabled { get; init; }
    public long ConfigurationBackupIntervalHours { get; init; } = 24;
    public string ConfigurationBackupPreferredTime { get; init; } = "02:00";
    public Timestamp? ConfigurationBackupLastRunAt { get; init; }
    public bool ProcessingPaused { get; init; }
    public Timestamp? ProcessingPausedUntil { get; init; }
    public bool ScanWhilePaused { get; init; } = true;
    public Timestamp UpdatedAt { get; init; }
}

/// <summary>A validated <c>PUT /suite/settings</c> (or bundle restore) request.</summary>
public sealed record SuiteSettingsUpdate(
    string AppTimezone,
    long LogRetentionDays,
    string? SetupWizardState = null,
    long? ActivityRetentionDays = null,
    bool? ConfigurationBackupEnabled = null,
    long? ConfigurationBackupIntervalHours = null,
    string? ConfigurationBackupPreferredTime = null);

/// <summary>Suite settings validation, normalisation and the response shape.</summary>
public static class SuiteSettingsRules
{
    private static readonly HashSet<string> WizardStates = new(StringComparer.Ordinal) { "pending", "skipped", "completed" };

    /// <summary>The setup wizard's starting state: skipped when users already exist, otherwise pending.</summary>
    public static string DefaultSetupWizardState(long existingUsers) => existingUsers > 0 ? "skipped" : "pending";

    /// <summary>
    /// Validates a settings update. Returns the normalised values, or throws
    /// <see cref="WireValueException"/> with the operator message.
    /// </summary>
    public static SuiteSettingsUpdate Normalize(SuiteSettingsUpdate update, ITimeZoneResolver zones)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(zones);
        var tz = (update.AppTimezone ?? string.Empty).Trim();
        if (tz.Length == 0)
        {
            throw new WireValueException("Timezone cannot be empty.");
        }

        if (!zones.TryFind(tz, out _))
        {
            throw new WireValueException("Choose a valid timezone (for example: UTC, Europe/London, America/New_York).");
        }

        if (update.LogRetentionDays is < 1 or > 3650)
        {
            throw new WireValueException("Log retention must be between 1 and 3650 days.");
        }

        if (update.ActivityRetentionDays is { } activity && activity is < 0 or > 3650)
        {
            throw new WireValueException("Activity history must be kept between 0 (forever) and 3650 days.");
        }

        string? wizard = null;
        if (update.SetupWizardState is not null)
        {
            wizard = update.SetupWizardState.Trim().ToLowerInvariant();
            if (!WizardStates.Contains(wizard))
            {
                throw new WireValueException("Setup wizard state must be pending, skipped, or completed.");
            }
        }

        if (update.ConfigurationBackupIntervalHours is { } hours && hours is < 1 or > 720)
        {
            throw new WireValueException("Backup interval must be between 1 and 720 hours.");
        }

        var backupTime = update.ConfigurationBackupPreferredTime is null
            ? null
            : NormalizeBackupPreferredTime(update.ConfigurationBackupPreferredTime);

        return update with
        {
            AppTimezone = tz,
            SetupWizardState = wizard,
            ConfigurationBackupPreferredTime = backupTime,
        };
    }

    /// <summary>The row with the normalised values assigned; optional fields left null keep their stored values.</summary>
    public static SuiteSettingsRecord Apply(SuiteSettingsRecord row, SuiteSettingsUpdate normalized)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(normalized);
        return row with
        {
            SetupWizardState = normalized.SetupWizardState ?? row.SetupWizardState,
            AppTimezone = normalized.AppTimezone,
            LogRetentionDays = normalized.LogRetentionDays,
            ActivityRetentionDays = normalized.ActivityRetentionDays ?? row.ActivityRetentionDays,
            ConfigurationBackupEnabled = normalized.ConfigurationBackupEnabled ?? row.ConfigurationBackupEnabled,
            ConfigurationBackupIntervalHours = normalized.ConfigurationBackupIntervalHours ?? row.ConfigurationBackupIntervalHours,
            ConfigurationBackupPreferredTime = normalized.ConfigurationBackupPreferredTime ?? row.ConfigurationBackupPreferredTime,
        };
    }

    /// <summary>Normalises the backup time: <c>H:M</c> → <c>HH:MM</c>.</summary>
    public static string NormalizeBackupPreferredTime(string? raw)
    {
        if (TryParseTime(raw, out var hour, out var minute))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hour:00}:{minute:00}");
        }

        throw new WireValueException("Backup time must use HH:MM in 24-hour time.");
    }

    /// <summary>Splits on the first colon and reads an integer hour (0-23) and minute (0-59); empty means 02:00.</summary>
    public static bool TryParseTime(string? raw, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            value = "02:00";
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 ||
            !WireConvert.TryParseIntLiteral(value[..colon], out var h) ||
            !WireConvert.TryParseIntLiteral(value[(colon + 1)..], out var m) ||
            h < 0 || h > 23 || m < 0 || m > 59)
        {
            return false;
        }

        hour = (int)h;
        minute = (int)m;
        return true;
    }

    /// <summary>The settings as the API returns them, with out-of-range stored values clamped.</summary>
    public static WireObject BuildOut(SuiteSettingsRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var wizard = (row.SetupWizardState ?? "pending").Trim().ToLowerInvariant();
        if (wizard.Length == 0 || !WizardStates.Contains(wizard))
        {
            wizard = "pending";
        }

        var tz = (row.AppTimezone ?? "UTC").Trim();
        var interval = row.ConfigurationBackupIntervalHours == 0 ? 24 : row.ConfigurationBackupIntervalHours;
        return new WireObject()
            .Set("setup_wizard_state", wizard)
            .Set("app_timezone", tz.Length == 0 ? "UTC" : tz)
            .Set("log_retention_days", Math.Max(1, Math.Min(row.LogRetentionDays, 3650)))
            .Set("activity_retention_days", Math.Max(0, Math.Min(row.ActivityRetentionDays, 3650)))
            .Set("configuration_backup_enabled", row.ConfigurationBackupEnabled)
            .Set("configuration_backup_interval_hours", Math.Max(1, Math.Min(interval, 720)))
            .Set("configuration_backup_preferred_time", NormalizeBackupPreferredTime(row.ConfigurationBackupPreferredTime))
            .Set("configuration_backup_last_run_at", row.ConfigurationBackupLastRunAt?.ToWireText())
            .Set("updated_at", row.UpdatedAt.ToWireText());
    }
}

/// <summary>The read-only security overview: the startup security options in plain language.</summary>
public static class SecurityOverview
{
    public static WireObject Build(WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var secretOk = (options.SessionSecret ?? string.Empty).Trim().Length > 0;
        return new WireObject()
            .Set("session_signing_configured", secretOk)
            .Set("sign_in_cookie_https_mode", SessionRules.SecureModeText(options.SessionCookieSecureMode))
            .Set("sign_in_cookie_https_plain", HttpsCookiePlain(options.SessionCookieSecureMode))
            .Set("sign_in_cookie_same_site", SameSitePlain(options.SessionCookieSameSite))
            .Set("standard_session_idle_timeout_plain", PlainDuration(Saturating(options.SessionIdleMinutes, 60)))
            .Set("standard_session_absolute_timeout_plain", PlainDuration(Saturating(options.SessionAbsoluteDays, 86400)))
            .Set("trusted_session_idle_timeout_plain", PlainDuration(Saturating(options.SessionTrustedIdleMinutes, 60)))
            .Set("trusted_session_absolute_timeout_plain", PlainDuration(Saturating(options.SessionTrustedAbsoluteDays, 86400)))
            .Set("extra_https_hardening_enabled", options.SecurityEnableHsts)
            .Set("sign_in_attempt_limit", options.AuthLoginRateMaxAttempts)
            .Set("sign_in_attempt_window_plain", PlainDuration(options.AuthLoginRateWindowSeconds))
            .Set("first_time_setup_attempt_limit", options.BootstrapRateMaxAttempts)
            .Set("first_time_setup_attempt_window_plain", PlainDuration(options.BootstrapRateWindowSeconds))
            .Set("allowed_browser_origins_count", options.CorsOrigins.Count)
            .Set("restart_required_note",
                "These safety options are read when the app starts from the server configuration file. " +
                "To change them, ask whoever runs the server to edit that file and restart the app.");
    }

    /// <summary>A duration in the largest whole unit that fits: seconds, minutes, hours or days.</summary>
    public static string PlainDuration(long seconds)
    {
        var s = Math.Max(1, seconds);
        if (s < 60)
        {
            return s == 1 ? "1 second" : $"{s} seconds";
        }

        if (s < 3600)
        {
            var m = s / 60;
            return m == 1 ? "1 minute" : $"{m} minutes";
        }

        if (s < 86400)
        {
            var h = s / 3600;
            return h == 1 ? "1 hour" : $"{h} hours";
        }

        var d = s / 86400;
        return d == 1 ? "1 day" : $"{d} days";
    }

    private static string SameSitePlain(CookieSameSite sameSite) => sameSite switch
    {
        CookieSameSite.Strict => "Strict (tighter; can break some flows)",
        CookieSameSite.None => "None (advanced; only makes sense with HTTPS)",
        _ => "Lax (recommended for most setups)",
    };

    private static string HttpsCookiePlain(CookieSecureMode mode) => mode switch
    {
        CookieSecureMode.Always => "Always on. Sign-in will not work unless the app is reached over HTTPS.",
        CookieSecureMode.Never => "Always off. The sign-in cookie is sent over plain HTTP as well as HTTPS.",
        _ => "Matched to each connection — on over HTTPS, off over plain HTTP on your network.",
    };

    private static long Saturating(long value, long factor) => value > long.MaxValue / factor ? long.MaxValue : value * factor;
}

/// <summary>The suite-wide pause, resolved against the clock.</summary>
public sealed record PauseState(bool Paused, Timestamp? PausedUntil, bool ScanWhilePaused, bool Expired = false)
{
    public const string InFlightPolicy = "Work already running finishes. Pausing stops Weir starting anything new.";

    public string Reason
    {
        get
        {
            if (!Paused)
            {
                return string.Empty;
            }

            return PausedUntil is { } until
                ? "Processing is paused. Weir will start work again automatically at " +
                  until.AsUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC."
                : "Processing is paused. Weir will start work again when you resume it.";
        }
    }

    public static PauseState Resolve(SuiteSettingsRecord row, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Resolve(row.ProcessingPaused, row.ProcessingPausedUntil, row.ScanWhilePaused, nowUtc);
    }

    /// <summary>
    /// Expiry is applied on read, so a pause set before a restart still lapses.
    /// A naive until time is UTC.
    /// </summary>
    public static PauseState Resolve(bool processingPaused, Timestamp? pausedUntil, bool scanWhilePaused, DateTime nowUtc)
    {
        Timestamp? until = pausedUntil is { } raw && !raw.IsAware ? raw with { Offset = TimeSpan.Zero } : pausedUntil;
        if (!processingPaused)
        {
            return new PauseState(false, null, scanWhilePaused);
        }

        if (until is { } u && nowUtc >= u.AsUtc)
        {
            return new PauseState(false, until, scanWhilePaused, Expired: true);
        }

        return new PauseState(true, until, scanWhilePaused);
    }

    public WireObject ToOut() => new WireObject()
        .Set("paused", Paused)
        .Set("paused_until", Paused ? PausedUntil?.ToWireText() : null)
        .Set("scan_while_paused", ScanWhilePaused)
        .Set("reason", Reason)
        .Set("in_flight_policy", InFlightPolicy);
}

/// <summary>When the automatic configuration snapshot is due.</summary>
public static class ConfigurationBackupSchedule
{
    public static bool IsDue(SuiteSettingsRecord suite, DateTime nowUtc, ITimeZoneResolver zones)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(zones);
        if (!suite.ConfigurationBackupEnabled)
        {
            return false;
        }

        var hours = suite.ConfigurationBackupIntervalHours == 0 ? 24 : suite.ConfigurationBackupIntervalHours;
        var intervalSeconds = Math.Max(3600, Math.Min(30L * 24 * 3600, hours > long.MaxValue / 3600 ? long.MaxValue : hours * 3600));
        var tzName = (suite.AppTimezone ?? string.Empty).Trim();
        if (tzName.Length == 0)
        {
            tzName = "UTC";
        }

        var zone = zones.TryFind(tzName, out var found) ? found : TimeZoneInfo.Utc;
        if (!SuiteSettingsRules.TryParseTime(suite.ConfigurationBackupPreferredTime, out var hour, out var minute))
        {
            hour = 2;
            minute = 0;
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var localTarget = new DateTime(localNow.Year, localNow.Month, localNow.Day, hour, minute, 0, DateTimeKind.Unspecified);
        if (suite.ConfigurationBackupLastRunAt is { } last)
        {
            var lastUtc = last.AsUtc;
            if ((nowUtc - lastUtc).TotalSeconds < intervalSeconds)
            {
                return false;
            }

            if (hours >= 24)
            {
                var lastLocal = TimeZoneInfo.ConvertTimeFromUtc(lastUtc, zone);
                if (lastLocal.Date == localNow.Date)
                {
                    return false;
                }
            }
        }

        return !(hours >= 24 && localNow < localTarget);
    }
}
