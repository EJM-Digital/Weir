using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using Weir.Core.Configuration;
using Weir.Core.Processing;
using Weir.TestSupport;

namespace Weir.Core.Tests.Configuration;

/// <summary>
/// <see cref="WeirOptionsLoader"/> over every spelling of a setting an operator might type: signs, underscores, padding, words,
/// numbers too big for a long, and text that is not a setting at all.
/// </summary>
public sealed partial class WeirOptionsLoaderPropertyTests
{
    /// <summary>A whole-number setting: its variable, what it is when unset, the range it is held to, and where it ends up.</summary>
    private sealed record WholeNumber(string Variable, long Default, long Minimum, long Maximum, Func<WeirOptions, long> Read);

    private sealed record Switch(string Variable, bool Default, Func<WeirOptions, bool> Read);

    private static readonly WholeNumber[] WholeNumbers =
    [
        new("WEIR_SESSION_IDLE_MINUTES", 20160, 1, long.MaxValue, o => o.SessionIdleMinutes),
        new("WEIR_SESSION_ABSOLUTE_DAYS", 90, 1, long.MaxValue, o => o.SessionAbsoluteDays),
        new("WEIR_SESSION_TRUSTED_ABSOLUTE_DAYS", 365, 1, long.MaxValue, o => o.SessionTrustedAbsoluteDays),
        new("WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS", 10, 1, long.MaxValue, o => o.AuthLoginRateMaxAttempts),
        new("WEIR_AUTH_LOGIN_RATE_WINDOW_SECONDS", 60, 1, long.MaxValue, o => o.AuthLoginRateWindowSeconds),
        new("WEIR_BOOTSTRAP_RATE_MAX_ATTEMPTS", 10, 1, long.MaxValue, o => o.BootstrapRateMaxAttempts),
        new("WEIR_BOOTSTRAP_RATE_WINDOW_SECONDS", 3600, 1, long.MaxValue, o => o.BootstrapRateWindowSeconds),
        new("WEIR_PROCESSING_JOB_LEASE_SECONDS", 300, 30, 86_400, o => o.ProcessingJobLeaseSeconds),
        new("WEIR_SQLITE_BUSY_TIMEOUT_SECONDS", 30, 1, 120, o => o.SqliteBusyTimeoutSeconds),
        new("WEIR_PROCESSING_PROBE_SIZE_MB", 10, 1, 1024, o => o.ProcessingProbeSizeMb),
        new("WEIR_PROCESSING_ANALYZE_DURATION_SECONDS", 10, 1, 300, o => o.ProcessingAnalyzeDurationSeconds),
        new("WEIR_PROCESSING_MOVIE_OUTPUT_CLEANUP_MIN_AGE_SECONDS", 172_800, 3600, 2_592_000, o => o.ProcessingMovieOutputCleanupMinAgeSeconds),
        new("WEIR_PROCESSING_TV_OUTPUT_CLEANUP_MIN_AGE_SECONDS", 172_800, 3600, 2_592_000, o => o.ProcessingTvOutputCleanupMinAgeSeconds),
        new("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MIN_STALE_AGE_SECONDS", 86_400, 60, 2_592_000, o => o.ProcessingWorkTempStaleSweepMinStaleAgeSeconds),
        new("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_INTERVAL_SECONDS", 3600, 60, 604_800, o => o.ProcessingWorkTempStaleSweepMovieScheduleIntervalSeconds),
        new("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_INTERVAL_SECONDS", 3600, 60, 604_800, o => o.ProcessingWorkTempStaleSweepTvScheduleIntervalSeconds),
        new("WEIR_JOB_ROWS_RETENTION_DAYS", 90, 1, 365, o => o.JobRowsRetentionDays),
        new("WEIR_JOB_ROWS_RETENTION_SCHEDULE_INTERVAL_SECONDS", 3600, 60, 86_400, o => o.JobRowsRetentionScheduleIntervalSeconds),
    ];

    private static readonly Switch[] Switches =
    [
        new("WEIR_SECURITY_ENABLE_HSTS", false, o => o.SecurityEnableHsts),
        new("WEIR_CHOWN_OUTPUT", false, o => o.OutputOwnershipChownEnabled),
        new("WEIR_PROCESSING_WATCHER_ENABLED", true, o => o.ProcessingWatcherEnabled),
        new("WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED", true, o => o.ManagerWorkflowSyncEnabled),
        new("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_SCHEDULE_ENABLED", true, o => o.ProcessingWatchedFolderRemuxScanDispatchScheduleEnabled),
        new("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_PERIODIC_ENQUEUE_REMUX_JOBS", true, o => o.ProcessingWatchedFolderRemuxScanDispatchPeriodicEnqueueRemuxJobs),
        new("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_ENABLED", false, o => o.ProcessingWorkTempStaleSweepMovieScheduleEnabled),
        new("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_ENABLED", false, o => o.ProcessingWorkTempStaleSweepTvScheduleEnabled),
    ];

    private static readonly Gen<string> Digits =
        from length in Gen.Choose(1, 6)
        from digits in Gen.ArrayOf(Gen.Elements("0123456789".ToCharArray()), length)
        select new string(digits);

    /// <summary>A whole number as it might be typed: a sign, groups of digits joined by underscores (sometimes wrongly), or something that is not a number.</summary>
    private static readonly Gen<string> WholeNumberText = Gen.Frequency(
        (8, NumberTyped()),
        (2, Gen.Elements("abc", "1.5", "1e3", "0x10", "１２", "- 5", "5 5", "--1", "+-1", "_", "-", "+", "1__2", "_1", "1_", "٣")),
        (1, Gen.Elements(string.Empty, "  ")));

    private static readonly Gen<string> SwitchText =
        from word in Gen.Elements("1", "true", "yes", "on", "0", "false", "no", "off", "2", "y", "tru", "enabled", "-1", string.Empty)
        from style in Gen.Elements(PathGenerators.CaseStyle.Keep, PathGenerators.CaseStyle.Upper, PathGenerators.CaseStyle.Lower)
        from padding in Gen.Elements(string.Empty, " ", "\t", " ")
        select padding + PathGenerators.Written(style, word) + padding;

    [Fact]
    public void A_whole_number_is_read_as_typed_and_held_to_its_range()
    {
        var cases = Gen.Elements(WholeNumbers).SelectMany(setting => Padded(Gen.OneOf(WholeNumberText, Gen.Elements(BoundaryTexts(setting)))).Select(text => (setting, text)));
        PropertyChecks.ForAll(cases, found =>
        {
            var (setting, text) = found;
            var expected = Math.Clamp(Parsed(text) ?? setting.Default, setting.Minimum, setting.Maximum);
            Assert.Equal(expected, setting.Read(Load((setting.Variable, text))));
        });
    }

    [Fact]
    public void Every_whole_number_setting_is_held_to_its_range_at_both_ends_of_it()
    {
        foreach (var setting in WholeNumbers)
        {
            foreach (var text in BoundaryTexts(setting))
            {
                var expected = Math.Clamp(Parsed(text)!.Value, setting.Minimum, setting.Maximum);
                Assert.True(expected == setting.Read(Load((setting.Variable, text))), $"{setting.Variable}={text}");
            }
        }
    }

    [Fact]
    public void Every_whole_number_setting_is_what_it_says_when_unset()
    {
        var unset = TestRuntime.Load();
        Assert.All(WholeNumbers, setting => Assert.Equal(setting.Default, setting.Read(unset)));
    }

    [Fact]
    public void The_trusted_idle_time_is_the_days_in_minutes_and_stops_at_the_largest_number()
    {
        PropertyChecks.ForAll(Padded(WholeNumberText), text =>
        {
            var days = Math.Max(1, Parsed(text) ?? 60);
            var expected = (long)BigInteger.Min(long.MaxValue, new BigInteger(days) * 1440);
            Assert.Equal(expected, Load(("WEIR_SESSION_TRUSTED_IDLE_DAYS", text)).SessionTrustedIdleMinutes);
        });
    }

    [Fact]
    public void The_number_of_files_at_once_is_whole_and_between_none_and_the_most_there_can_be_with_less_than_none_meaning_one()
    {
        PropertyChecks.ForAll(Padded(WholeNumberText), text =>
        {
            var raw = Parsed(text) ?? OperatorSettingsRules.MaxFilesAtOnce;
            var expected = raw < 0 ? 1 : Math.Min(OperatorSettingsRules.MaxFilesAtOnce, raw);
            Assert.Equal(expected, Load(("WEIR_PROCESSING_WORKER_COUNT", text)).ProcessingWorkerCount);
        });
    }

    [Fact]
    public void The_watcher_delay_is_a_whole_number_of_seconds_from_a_quarter_to_five_minutes()
    {
        PropertyChecks.ForAll(Padded(WholeNumberText), text =>
        {
            var expected = Math.Max(0.25, Math.Min(300.0, Parsed(text) ?? 3));
            Assert.Equal(expected, Load(("WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS", text)).ProcessingWatcherDebounceSeconds);
        });
    }

    [Fact]
    public void A_clamp_keeps_a_number_inside_its_range_and_leaves_one_that_is_inside_alone()
    {
        var clamps = new (Func<long, int> Clamp, long Minimum, long Maximum)[]
        {
            (WeirOptionsLoader.ClampProcessingJobLeaseSeconds, 30, 86_400),
            (WeirOptionsLoader.ClampSqliteBusyTimeoutSeconds, 1, 120),
            (WeirOptionsLoader.ClampProcessingScheduleIntervalSeconds, 60, 604_800),
        };
        var edges = Gen.Elements(long.MinValue, long.MaxValue, -1L, 0L, 1L, int.MaxValue + 1L, int.MinValue - 1L);
        var anywhere = Gen.OneOf(edges, Gen.Choose(-1000, 700_000).Select(number => (long)number));

        PropertyChecks.ForAll(Gen.Elements(clamps).Zip(anywhere), found =>
        {
            var ((clamp, minimum, maximum), raw) = found;
            var clamped = clamp(raw);
            Assert.InRange(clamped, minimum, maximum);
            if (raw >= minimum && raw <= maximum)
            {
                Assert.Equal(raw, clamped);
            }
            else
            {
                Assert.Equal(raw < minimum ? minimum : maximum, clamped);
            }

            Assert.Equal(clamped, clamp(clamped));
        });
    }

    [Fact]
    public void A_switch_is_on_off_or_what_it_is_when_the_text_says_neither()
    {
        var cases = Gen.Elements(Switches).Zip(SwitchText);
        PropertyChecks.ForAll(cases, found =>
        {
            var (setting, text) = found;
            var word = text.Trim().ToLowerInvariant();
            var expected = word switch
            {
                "1" or "true" or "yes" or "on" => true,
                "0" or "false" or "no" or "off" => false,
                _ => setting.Default,
            };
            Assert.Equal(expected, setting.Read(Load((setting.Variable, text))));
        });
    }

    [Fact]
    public void A_list_is_split_at_commas_trimmed_and_cleared_of_blanks_and_reading_it_again_changes_nothing()
    {
        var item = Gen.Elements("a", "http://localhost:3000", " b ", string.Empty, "  ", "x y", "\t", "é");
        var lists = Gen.Choose(0, 6).SelectMany(count => Gen.ArrayOf(item, count));

        PropertyChecks.ForAll(lists, items =>
        {
            var parsed = WeirOptionsLoader.ParseCsv(string.Join(',', items));
            Assert.Equal(items.Select(part => part.Trim()).Where(part => part.Length > 0), parsed);
            Assert.Equal(parsed, WeirOptionsLoader.ParseCsv(string.Join(',', parsed)));
        });
    }

    [Fact]
    public void A_wildcard_origin_stops_the_start_and_no_other_list_of_origins_does()
    {
        var origin = Gen.Elements("*", " * ", "https://a.example", "http://localhost:5173", "http://127.0.0.1", "a*", "**");
        var lists = Gen.Choose(0, 4).SelectMany(count => Gen.ArrayOf(origin, count));
        PropertyChecks.ForAll(lists.Zip(Gen.Elements("production", "development")), found =>
        {
            var (origins, environment) = found;
            var runtime = RuntimeWith(("WEIR_CORS_ORIGINS", string.Join(',', origins)), ("WEIR_ENV", environment));
            if (origins.Any(entry => entry.Trim() == "*"))
            {
                Assert.Equal(WeirOptionsLoader.CorsWildcardMessage, Assert.Throws<WeirConfigurationException>(() => WeirOptionsLoader.Load(runtime)).Message);
            }
            else
            {
                var loaded = WeirOptionsLoader.Load(runtime).CorsOrigins;
                var typed = origins.Select(entry => entry.Trim()).Where(entry => entry.Length > 0).Select(entry => entry.TrimEnd('/')).Distinct();
                Assert.Empty(typed.Except(loaded));
                if (environment == "production")
                {
                    Assert.Equal(origins.Count(entry => entry.Trim().Length > 0), loaded.Count);
                }
            }
        });
    }

    [Fact]
    public void A_file_mode_is_three_or_four_octal_digits_or_it_is_refused_by_name()
    {
        var mode = Gen.Choose(0, 5).SelectMany(length => Gen.ArrayOf(Gen.Elements("01234567890189 ".ToCharArray()), length)).Select(chars => new string(chars));
        PropertyChecks.ForAll(mode, text =>
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                Assert.Null(WeirOptionsLoader.ParseOctalMode(text, "WEIR_FILE_MODE_OUTPUT"));
            }
            else if (trimmed.Length is 3 or 4 && trimmed.All(digit => digit is >= '0' and <= '7'))
            {
                Assert.Equal((UnixFileMode)Convert.ToInt32(trimmed, 8), WeirOptionsLoader.ParseOctalMode(text, "WEIR_FILE_MODE_OUTPUT"));
            }
            else
            {
                Assert.Contains("WEIR_FILE_MODE_OUTPUT", Assert.Throws<WeirConfigurationException>(() => WeirOptionsLoader.ParseOctalMode(text, "WEIR_FILE_MODE_OUTPUT")).Message, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void The_artwork_gateway_is_off_an_address_without_a_trailing_slash_or_refused()
    {
        var address = from scheme in Gen.Elements("http", "https", "ftp", "file", "", "HTTP")
                      from host in Gen.Elements("gateway.example", "localhost:8080", "", "[::1]:99", "a b")
                      from path in Gen.Elements(string.Empty, "/", "/v1", "/v1/", "//")
                      select scheme + "://" + host + path;
        var text = Gen.OneOf(address, Gen.Elements("off", "OFF", " off ", string.Empty, "gateway.example", "http:/x"));

        PropertyChecks.ForAll(text, raw =>
        {
            var value = raw.Trim();
            if (value.Length == 0)
            {
                Assert.Equal(Weir.Core.Artwork.ArtworkPosterSource.DefaultGatewayUrl, WeirOptionsLoader.ParseArtworkGatewayUrl(raw));
            }
            else if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(string.Empty, WeirOptionsLoader.ParseArtworkGatewayUrl(raw));
            }
            else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0)
            {
                var gateway = WeirOptionsLoader.ParseArtworkGatewayUrl(raw);
                Assert.Equal(value.TrimEnd('/'), gateway);
                Assert.False(gateway.EndsWith('/'));
            }
            else
            {
                Assert.Throws<WeirConfigurationException>(() => WeirOptionsLoader.ParseArtworkGatewayUrl(raw));
            }
        });
    }

    [Fact]
    public void Whatever_the_environment_holds_the_settings_load_or_the_start_is_refused_with_a_message()
    {
        var variables = new[]
        {
            "WEIR_ENV", "WEIR_LOG_LEVEL", "WEIR_CORS_ORIGINS", "WEIR_TRUSTED_BROWSER_ORIGINS", "WEIR_TRUSTED_PROXY_IPS", "WEIR_ALLOWED_HOSTS",
            "WEIR_SESSION_COOKIE_SAMESITE", "WEIR_SESSION_COOKIE_SECURE", "WEIR_HOME", "WEIR_DB_PATH", "WEIR_BACKUP_DIR", "WEIR_LOG_DIR",
            "WEIR_TEMP_DIR", "WEIR_WEB_DIST", "WEIR_FILE_MODE_OUTPUT", "WEIR_DIR_MODE_OUTPUT", "WEIR_ARTWORK_GATEWAY_URL",
            "WEIR_ARR_RADARR_BASE_URL", "WEIR_ARR_SONARR_BASE_URL", "WEIR_LIBRARY_SWAP_HOLD_AT", "WEIR_PUID", "PUID", "WEIR_PGID",
            "WEIR_PREVIOUS_CREDENTIALS_SECRETS", "WEIR_CREDENTIALS_SECRET", "WEIR_SESSION_IDLE_MINUTES", "WEIR_PROCESSING_WORKER_COUNT",
        };
        var soup = Gen.Choose(0, 14).SelectMany(length => Gen.ArrayOf(Gen.Elements("aZ09/\\:.,*_-~ \t[]@?#%+;'\"|<>=\0é".ToCharArray()), length)).Select(chars => new string(chars));
        var present = Gen.OneOf(Gen.Constant<string?>(null), soup.Select(text => (string?)text));
        var environments = Gen.ArrayOf(present, variables.Length);

        PropertyChecks.ForAll(environments, values =>
        {
            var set = variables.Zip(values).Where(pair => pair.Second is not null).Select(pair => (pair.First, pair.Second!)).ToArray();
            try
            {
                WeirOptionsLoader.Load(RuntimeWith(set));
            }
            catch (WeirConfigurationException)
            {
                // Refused with a message an operator can act on.
            }
        });
    }

    /// <summary>The numbers either side of the ends of a setting's range, where a clamp that is a little off shows.</summary>
    private static string[] BoundaryTexts(WholeNumber setting)
    {
        BigInteger[] numbers =
        [
            setting.Minimum - 1,
            setting.Minimum,
            setting.Minimum + 1,
            setting.Maximum - 1,
            setting.Maximum,
            new BigInteger(setting.Maximum) + 1,
        ];
        return [.. numbers.Select(number => number.ToString(CultureInfo.InvariantCulture))];
    }

    private static Gen<string> NumberTyped() =>
        from sign in Gen.Elements(string.Empty, string.Empty, "+", "-")
        from count in Gen.Choose(1, 4)
        from groups in Gen.ArrayOf(Digits, count)
        from glue in Gen.ArrayOf(Gen.Elements(string.Empty, "_", "_", "__"), count - 1)
        from lead in Gen.Elements(string.Empty, string.Empty, string.Empty, "_")
        from trail in Gen.Elements(string.Empty, string.Empty, string.Empty, "_")
        select sign + lead + string.Concat(groups.Select((digits, index) => digits + (index < glue.Length ? glue[index] : string.Empty))) + trail;

    private static Gen<string> Padded(Gen<string> text) =>
        from inner in text
        from before in Gen.Elements(string.Empty, string.Empty, " ", "\t", " ")
        from after in Gen.Elements(string.Empty, string.Empty, " ", "\r\n")
        select before + inner + after;

    /// <summary>The number the text means: optional sign and digits with single underscores between them, saturating at the ends of a long.</summary>
    private static long? Parsed(string text)
    {
        var trimmed = text.Trim();
        if (!NumberPattern().IsMatch(trimmed))
        {
            return null;
        }

        var value = BigInteger.Parse(trimmed.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return (long)BigInteger.Min(long.MaxValue, BigInteger.Max(long.MinValue, value));
    }

    private static WeirOptions Load(params (string Name, string Value)[] variables) => WeirOptionsLoader.Load(RuntimeWith(variables));

    private static RuntimeEnvironment RuntimeWith(params (string Name, string Value)[] variables) => TestRuntime.With(variables);

    [GeneratedRegex("^[+-]?[0-9]+(_[0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
