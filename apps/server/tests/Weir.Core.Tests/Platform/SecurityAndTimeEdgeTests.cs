using System.Text;
using Weir.Core.Json;
using Weir.Core.Observability;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Core.Security;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Core.Updates;

namespace Weir.Core.Tests.Platform;

/// <summary>The last edges of secrets, times, updates and what a repeat of a file says: values left out, cut short or out of range.</summary>
public sealed class SecurityAndTimeEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_copy_a_manager_collected_says_when_it_did_so_when_it_said_when()
    {
        var withTime = new CleanedEarlier("D:\\Clean\\Film.mkv", Now, true, "Radarr", Now.AddDays(1));
        var withoutTime = withTime with { CollectedAt = null };

        Assert.Equal($"Already imported: Radarr collected the cleaned copy on {Now.AddDays(1):yyyy-MM-dd}", withTime.Reason);
        Assert.Equal($"Already imported: Radarr collected the cleaned copy on {Now:yyyy-MM-dd}", withoutTime.Reason);
    }

    [Fact]
    public void An_event_with_no_counts_carries_none()
    {
        var withNone = new DiagnosticEvent("processing", "scan", "worker", "completed", "info").AsSafeDict();

        Assert.Null(withNone.Get("counts"));
    }

    [Fact]
    public void An_envelope_with_no_key_id_or_an_empty_one_is_opened_with_the_session_secret()
    {
        var cipher = new CredentialCipher(null, "session-secret", [], TimeProvider.System);
        var token = CredentialCipher.ForSecret("session-secret")!.Encrypt(Encoding.UTF8.GetBytes("arr-api-key"), TimeProvider.System);

        foreach (var keyId in new WireValue[] { WireNull.Instance, new WireString(string.Empty) })
        {
            var envelope = WireJsonWriter.Dumps(new WireObject().Set("version", 2L).Set("key_id", keyId).Set("token", token), WireJsonFormat.Compact);

            Assert.Equal("arr-api-key", cipher.Decrypt(envelope));
        }
    }

    [Theory]
    [InlineData("""{"format": "1"}""")]
    [InlineData("""{"kdf": "argon2id"}""")]
    [InlineData("""{"format": 1, "kdf": "argon2id", "memory_kib": 8.5}""")]
    public void A_sealed_section_with_a_number_that_is_missing_or_not_whole_cannot_be_read(string section)
    {
        Assert.Throws<WireValueException>(() => BackupSecretsSeal.TryOpen(WireJsonParser.Parse(section), "password"));
    }

    [Theory]
    [InlineData("2026-01-01T10:00:00x")]
    [InlineData("2026-01-01T10:00:00+0")]
    [InlineData("2026-01-01T10")]
    public void A_time_with_something_left_over_or_cut_short_is_not_a_timestamp_unless_it_ends_after_the_hour(string text)
    {
        Assert.Equal(text == "2026-01-01T10", Timestamp.TryFromIsoFormat(text, out _));
    }

    [Fact]
    public void A_zone_the_systems_data_does_not_have_is_not_found()
    {
        Assert.False(TimeZones.TryFind("Europe/London", (string name, out TimeZoneInfo zone) => { zone = TimeZoneInfo.Utc; return false; }, out var found));
        Assert.Equal(TimeZoneInfo.Utc, found);
        Assert.True(TimeZones.TryFind("Europe/London", (string name, out TimeZoneInfo zone) => { zone = TimeZoneInfo.Local; return true; }, out var local));
        Assert.Equal(TimeZoneInfo.Local, local);
    }

    [Theory]
    [InlineData("es-419", "spa-419")]
    [InlineData("es-abc", null)]
    [InlineData("es-12", null)]
    [InlineData("es-1234", null)]
    [InlineData("pt-BR", "por-BR")]
    [InlineData("zh-Hant", "zho-Hant")]
    public void A_language_tag_gives_a_variant_only_when_its_region_or_script_part_is_one_the_table_knows(string tag, string? identifier)
    {
        Assert.Equal(identifier, LanguageVariants.Detect(null, null, tag).Identifier);
    }

    [Fact]
    public void A_title_beyond_a_tracks_own_is_compared_only_when_the_track_has_none_in_its_tags()
    {
        var video = ProbeStreamInfo.Parse("""{"index": 0, "codec_type": "video", "codec_name": "h264"}""");
        var audio = ProbeStreamInfo.Parse("""{"index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2, "disposition": {"default": 1}, "tags": {"language": "eng"}}""");
        var subtitle = ProbeStreamInfo.Parse("""{"index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": {"language": "eng"}}""");
        var metadata = new MetadataRules { StandardizeTrackNames = true };
        var config = RemuxRules.DefaultConfig() with { SubtitleMode = RemuxRuleValues.SubtitleModeKeepSelected, SubtitleLangs = ["eng"], Metadata = metadata };
        var plan = RemuxRules.PlanRemux([video], [audio], [subtitle], config)!;
        var named = ProbeStreamInfo.Parse(
            "{\"index\": 1, \"codec_type\": \"audio\", \"codec_name\": \"aac\", \"channels\": 2, \"disposition\": {\"default\": 1}, \"tags\": {\"language\": \"eng\", \"title\": \"" +
            TrackNaming.RenderTrackName(metadata, plan.Audio[0]) + "\"}}");

        Assert.True(RemuxRules.IsRemuxRequired(plan, [named], [subtitle]));
    }

    [Theory]
    [InlineData("""{"pending_version": 5, "last_checked_at": "2026-10-02T12:00:00Z"}""")]
    public void A_pending_version_that_is_not_text_is_left_out_of_a_step(string state)
    {
        Assert.Null(((WireObject)WireJsonParser.Parse(state)).Get("missing"));
        Assert.IsType<WireNull>(UpdateStatus.WithStep((WireObject)WireJsonParser.Parse(state), "checking").Get("pending_version"));
    }

    [Fact]
    public void Settings_go_out_with_the_last_backup_time_when_there_was_one()
    {
        var row = new SuiteSettingsRecord { ConfigurationBackupLastRunAt = Timestamp.FromDateTimeOffset(Now) };

        Assert.Equal(Timestamp.FromDateTimeOffset(Now).ToWireText(), ((WireString)SuiteSettingsRules.BuildOut(row).Get("configuration_backup_last_run_at")!).Value);
        Assert.IsType<WireNull>(SuiteSettingsRules.BuildOut(new SuiteSettingsRecord()).Get("configuration_backup_last_run_at"));
    }

    [Fact]
    public void A_backup_schedule_for_a_settings_row_with_no_timezone_runs_in_utc()
    {
        var suite = new SuiteSettingsRecord { ConfigurationBackupEnabled = true, AppTimezone = null! };

        Assert.True(ConfigurationBackupSchedule.IsDue(suite, Now.UtcDateTime, new IanaTimeZoneResolver()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5L)]
    [InlineData("")]
    public void A_pending_version_that_is_missing_or_not_text_leaves_the_last_check_alone(object? pending)
    {
        var state = new WireObject().Set("last_checked_at", "2026-10-02T12:00:00Z");
        if (pending is not null)
        {
            state.Set("pending_version", pending is long number ? new WireInteger(number) : new WireString((string)pending));
        }

        var stepped = UpdateStatus.WithStep(state, "checking");

        Assert.Equal("2026-10-02T12:00:00Z", ((WireString)stepped.Get("last_checked_at")!).Value);
    }

    [Fact]
    public void A_truthy_value_is_one_that_is_present_and_not_empty()
    {
        Assert.False(WireConvert.Truthy(null));
        Assert.False(WireConvert.Truthy(WireBool.False));
        Assert.True(WireConvert.Truthy(WireBool.True));
    }
}
