using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>
/// What a restore does with the shape of a backup: the format it accepts, the sections it needs, the limits it holds the
/// settings to, how each column type is read, and that a row it cannot restore stops the restore with a plain reason.
/// </summary>
public sealed class ConfigurationBundleRestoreRulesTests : IDisposable
{
    private const string ArrTable = "arr_library_operator_settings";
    private const string OperatorTable = "operator_settings";
    private const string BoolColumn = "sonarr_missing_search_enabled";
    private const string NullableBoolColumn = "sonarr_last_connection_test_ok";
    private const string TimeColumn = "sonarr_last_connection_test_at";

    private readonly StoreFixture _store = new();
    private readonly ITimeZoneResolver _zones = new IanaTimeZoneResolver();
    private readonly ConfigurationBundleStore _bundle;

    public ConfigurationBundleRestoreRulesTests()
    {
        _bundle = new ConfigurationBundleStore(new SuiteSettingsStore(_store.Users), BackupParts.Connections());
    }

    public void Dispose() => _store.Dispose();

    private Task<WireObject> BuildAsync() => _store.WithUnitOfWork(_bundle.BuildAsync, commit: false);

    private Task<bool> ApplyAsync(WireObject bundle) => _store.WithUnitOfWork(async uow =>
    {
        await _bundle.ApplyAsync(uow, bundle, _zones, _store.Options.WeirHome);
        return true;
    });

    private static WireObject Section(WireObject bundle, string name) => (WireObject)bundle[name];

    public static TheoryData<WireValue, bool> FormatVersions => new()
    {
        { new WireNumber(4.0), true },
        { new WireInteger(4), true },
        { new WireNumber(3.0), false },
        { new WireInteger(3), false },
        { new WireString("4"), false },
        { WireNull.Instance, false },
    };

    [Theory]
    [MemberData(nameof(FormatVersions))]
    public async Task A_backup_is_restored_only_when_its_format_version_is_the_one_this_version_writes(WireValue version, bool restores)
    {
        var bundle = await BuildAsync();
        bundle.Set("format_version", version);

        if (restores)
        {
            Assert.True(await ApplyAsync(bundle));
            return;
        }

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));
        Assert.Contains("cannot restore", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("suite_settings")]
    [InlineData(ArrTable)]
    [InlineData(OperatorTable)]
    public async Task A_backup_missing_one_of_the_three_settings_sections_is_refused_as_incomplete(string section)
    {
        var bundle = await BuildAsync();
        bundle.Remove(section);

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Contains("not a complete Weir backup", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ArrTable)]
    [InlineData(OperatorTable)]
    public async Task An_export_is_refused_when_a_settings_row_is_missing_from_the_database(string table)
    {
        await _store.Execute($"DELETE FROM {table}");

        var exception = await Assert.ThrowsAsync<WireValueException>(BuildAsync);

        Assert.Contains($"Missing required configuration row: {table}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("suite_settings")]
    [InlineData(ArrTable)]
    [InlineData(OperatorTable)]
    public async Task A_settings_section_that_is_not_an_object_is_refused(string section)
    {
        var bundle = await BuildAsync();
        bundle.Set(section, "not an object");

        var exception = await Assert.ThrowsAsync<WireTypeException>(() => ApplyAsync(bundle));

        Assert.Contains("must be an object", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app_timezone")]
    [InlineData("log_retention_days")]
    public async Task A_suite_section_without_a_required_setting_is_refused_by_name(string key)
    {
        var bundle = await BuildAsync();
        Section(bundle, "suite_settings").Remove(key);

        var exception = await Assert.ThrowsAsync<WireTypeException>(() => ApplyAsync(bundle));

        Assert.Contains($"missing {key}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("activity_retention_days", -1, "Activity history must be kept between 0 (forever) and 3650 days.")]
    [InlineData("activity_retention_days", 3651, "Activity history must be kept between 0 (forever) and 3650 days.")]
    [InlineData("configuration_backup_interval_hours", 0, "Backup interval must be between 1 and 720 hours.")]
    [InlineData("configuration_backup_interval_hours", 721, "Backup interval must be between 1 and 720 hours.")]
    public async Task A_retention_or_backup_interval_outside_its_limits_is_refused_with_the_limit_in_the_message(string key, long value, string message)
    {
        var bundle = await BuildAsync();
        Section(bundle, "suite_settings").Set(key, value);

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public async Task A_suite_section_restores_its_retention_backup_schedule_and_direct_play_devices()
    {
        var bundle = await BuildAsync();
        Section(bundle, "suite_settings")
            .Set("activity_retention_days", 30L)
            .Set("configuration_backup_enabled", true)
            .Set("configuration_backup_interval_hours", 12L)
            .Set("configuration_backup_preferred_time", "03:30")
            .Set("direct_play_devices", "living room tv");

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM suite_settings WHERE activity_retention_days = 30 AND configuration_backup_enabled = 1 AND configuration_backup_interval_hours = 12 AND configuration_backup_preferred_time = '03:30' AND direct_play_devices = 'living room tv'"));
    }

    [Theory]
    [InlineData("libraries")]
    [InlineData("rule_sets")]
    public async Task A_rows_section_holding_something_other_than_objects_is_refused(string section)
    {
        var bundle = await BuildAsync();
        bundle.Set(section, new WireArray([new WireString("a row")]));

        var exception = await Assert.ThrowsAsync<WireTypeException>(() => ApplyAsync(bundle));

        Assert.Contains("must be an object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rows_section_written_as_an_object_or_text_is_read_as_its_keys_or_letters_and_refused_as_not_rows()
    {
        var asObject = await BuildAsync();
        asObject.Set("libraries", new WireObject().Set("Movies", true));
        var asText = await BuildAsync();
        asText.Set("rule_sets", "abc");

        Assert.Contains("must be an object", (await Assert.ThrowsAsync<WireTypeException>(() => ApplyAsync(asObject))).Message, StringComparison.Ordinal);
        Assert.Contains("must be an object", (await Assert.ThrowsAsync<WireTypeException>(() => ApplyAsync(asText))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rows_section_that_is_a_number_is_refused_as_not_a_list()
    {
        var bundle = await BuildAsync();
        bundle.Set("libraries", 7L);

        var exception = await Assert.ThrowsAsync<WireTypeException>(() => ApplyAsync(bundle));

        Assert.Contains("must be a list", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_settings_row_missing_from_the_database_is_added_back_from_the_backup()
    {
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Set(BoolColumn, true);
        await _store.Execute($"DELETE FROM {ArrTable}");

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar($"SELECT count(*) FROM {ArrTable} WHERE id = 1 AND {BoolColumn} = 1"));
    }

    [Fact]
    public async Task A_settings_section_with_no_known_column_adds_the_row_with_its_defaults()
    {
        var bundle = await BuildAsync();
        bundle.Set(ArrTable, new WireObject().Set("not_a_column", "ignored"));
        await _store.Execute($"DELETE FROM {ArrTable}");

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar($"SELECT count(*) FROM {ArrTable} WHERE {BoolColumn} = 0"));
    }

    public static TheoryData<WireValue, long> TrueLikeValues => new()
    {
        { WireBool.True, 1 },
        { new WireInteger(1), 1 },
        { new WireNumber(1.0), 1 },
        { WireBool.False, 0 },
        { new WireInteger(0), 0 },
        { new WireNumber(0.0), 0 },
    };

    [Theory]
    [MemberData(nameof(TrueLikeValues))]
    public async Task A_flag_is_restored_from_a_boolean_or_from_zero_or_one(WireValue value, long stored)
    {
        await _store.Execute($"UPDATE {ArrTable} SET {BoolColumn} = {1 - stored}");
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Set(BoolColumn, value);

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar($"SELECT count(*) FROM {ArrTable} WHERE {BoolColumn} = {stored}"));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData(2L)]
    public async Task A_flag_that_is_neither_a_boolean_nor_zero_or_one_is_refused(object value)
    {
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Set(BoolColumn, value is long number ? WireValue.Of(number) : WireValue.Of((string)value));

        var exception = await Assert.ThrowsAsync<WireTypeException>(() => ApplyAsync(bundle));

        Assert.Contains("Not a boolean value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_optional_flag_is_restored_as_unset()
    {
        await _store.Execute($"UPDATE {ArrTable} SET {NullableBoolColumn} = 1");
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Set(NullableBoolColumn, WireNull.Instance);

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar($"SELECT count(*) FROM {ArrTable} WHERE {NullableBoolColumn} IS NULL"));
    }

    [Fact]
    public async Task A_backup_without_a_modified_time_moves_the_row_s_only_when_a_setting_changes()
    {
        await _store.Execute($"UPDATE {ArrTable} SET updated_at = '2000-01-01 00:00:00'");
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Remove("updated_at");
        await ApplyAsync(bundle);
        Assert.Equal(1, await _store.Scalar($"SELECT count(*) FROM {ArrTable} WHERE updated_at LIKE '2000-01-01%'"));

        Section(bundle, ArrTable).Set(BoolColumn, true);
        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar($"SELECT count(*) FROM {ArrTable} WHERE updated_at NOT LIKE '2000-01-01%' AND {BoolColumn} = 1"));
    }

    [Fact]
    public async Task A_timestamp_is_restored_whether_it_ends_in_Z_or_an_offset_and_is_left_alone_when_the_same_instant_comes_back()
    {
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Set(TimeColumn, "2026-10-01T10:00:00Z");
        await ApplyAsync(bundle);

        var exported = await BuildAsync();
        Assert.Equal("2026-10-01T10:00:00+00:00", ((WireString)Section(exported, ArrTable)[TimeColumn]).Value);

        Section(exported, ArrTable).Set(TimeColumn, "2026-10-01T12:00:00+02:00");
        await ApplyAsync(exported);
        Assert.Equal("2026-10-01T10:00:00+00:00", ((WireString)Section(await BuildAsync(), ArrTable)[TimeColumn]).Value);
    }

    [Fact]
    public async Task A_timestamp_written_without_an_offset_is_kept_as_the_clock_time_it_names()
    {
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Set(TimeColumn, "2026-10-01T10:00:00");
        await ApplyAsync(bundle);
        var first = ((WireString)Section(await BuildAsync(), ArrTable)[TimeColumn]).Value;

        await ApplyAsync(await BuildAsync());

        Assert.Equal(first, ((WireString)Section(await BuildAsync(), ArrTable)[TimeColumn]).Value);
        Assert.StartsWith("2026-10-01T10:00:00", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timestamp_that_is_not_text_is_restored_as_unset()
    {
        await _store.Execute($"UPDATE {ArrTable} SET {TimeColumn} = '2026-10-01 10:00:00'");
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Set(TimeColumn, 12345L);

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar($"SELECT count(*) FROM {ArrTable} WHERE {TimeColumn} IS NULL"));
    }

    [Fact]
    public async Task A_timestamp_that_is_not_a_date_is_refused_and_quoted_back()
    {
        var bundle = await BuildAsync();
        Section(bundle, ArrTable).Set(TimeColumn, "last tuesday");

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Contains("last tuesday", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_timestamp_that_is_not_a_date_stops_the_restore_rather_than_being_overwritten_unseen()
    {
        var bundle = await BuildAsync();
        await _store.Execute($"UPDATE {ArrTable} SET {TimeColumn} = 'garbage'");

        await Assert.ThrowsAsync<FormatException>(() => ApplyAsync(bundle));
    }

    [Fact]
    public async Task Number_settings_that_differ_are_rewritten_and_the_same_ones_are_left_alone()
    {
        var bundle = await BuildAsync();
        Section(bundle, OperatorTable).Set("max_concurrent_files", 3L).Set("file_log_retention_days", 30L);

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar($"SELECT count(*) FROM {OperatorTable} WHERE max_concurrent_files = 3 AND file_log_retention_days = 30"));
    }
}
