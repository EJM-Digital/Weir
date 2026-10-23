using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>Restoring backups that leave out sections, or carry numbers in forms the current export does not write.</summary>
public sealed class ConfigurationBundleEdgeTests : IDisposable
{
    private readonly StoreFixture _store = new();
    private readonly ITimeZoneResolver _zones = new IanaTimeZoneResolver();
    private readonly ConfigurationBundleStore _bundle;

    public ConfigurationBundleEdgeTests()
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

    [Fact]
    public async Task A_backup_with_no_workflows_section_leaves_the_workflows_as_they_are()
    {
        var bundle = await BuildAsync();
        var before = await _store.Scalar("SELECT count(*) FROM libraries");
        bundle.Remove("libraries");

        await ApplyAsync(bundle);

        Assert.Equal(before, await _store.Scalar("SELECT count(*) FROM libraries"));
    }

    [Fact]
    public async Task A_backup_with_no_workflows_and_no_rule_sets_section_empties_both()
    {
        var bundle = await BuildAsync();
        bundle.Set("libraries", new WireArray());
        bundle.Remove("rule_sets");

        await ApplyAsync(bundle);

        Assert.Equal(0, await _store.Scalar("SELECT count(*) FROM libraries"));
        Assert.Equal(0, await _store.Scalar("SELECT count(*) FROM rule_sets"));
    }

    [Fact]
    public async Task A_workflow_in_a_backup_that_leaves_out_its_choices_takes_the_usual_ones()
    {
        var bundle = await BuildAsync();
        var workflows = ((WireArray)bundle["libraries"]).Items.Cast<WireObject>().ToList();
        foreach (var workflow in workflows)
        {
            foreach (var choice in new[] { "rejected_file_action", "output_collision_policy", "ffmpeg_strictness", "remux_writer", "failure_policy" })
            {
                workflow.Remove(choice);
            }
        }

        await ApplyAsync(bundle);

        Assert.Equal(workflows.Count, await _store.Scalar("SELECT count(*) FROM libraries"));
    }

    [Fact]
    public async Task A_backup_whose_settings_leave_out_the_optional_choices_restores_without_them()
    {
        var bundle = await BuildAsync();
        var settings = (WireObject)bundle["suite_settings"];
        foreach (var choice in new[] { "configuration_backup_enabled", "configuration_backup_interval_hours", "configuration_backup_preferred_time", "activity_retention_days" })
        {
            settings.Remove(choice);
        }

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM suite_settings"));
    }

    [Fact]
    public async Task A_backup_whose_settings_carry_the_optional_choices_restores_them()
    {
        var bundle = await BuildAsync();
        var settings = (WireObject)bundle["suite_settings"];
        settings.Set("configuration_backup_enabled", true).Set("configuration_backup_interval_hours", 12L).Set("configuration_backup_preferred_time", "03:30").Set("activity_retention_days", 90L);

        await ApplyAsync(bundle);

        Assert.Equal(12, await _store.Scalar("SELECT configuration_backup_interval_hours FROM suite_settings"));
        Assert.Equal(90, await _store.Scalar("SELECT activity_retention_days FROM suite_settings"));
    }

    [Theory]
    [InlineData("log_retention_days", "1000000000000000000000000000000", "Log retention")]
    [InlineData("activity_retention_days", "-1000000000000000000000000000000", "Activity history must be kept")]
    [InlineData("activity_retention_days", "1000000000000000000000000000000", "Activity history must be kept")]
    [InlineData("configuration_backup_interval_hours", "-1000000000000000000000000000000", "Backup interval must be")]
    [InlineData("configuration_backup_interval_hours", "1000000000000000000000000000000", "Backup interval must be")]
    public async Task A_number_in_a_backup_too_big_to_be_a_number_of_days_or_hours_is_refused_in_plain_words(string setting, string digits, string expected)
    {
        var bundle = await BuildAsync();
        ((WireObject)bundle["suite_settings"]).Set(setting, new WireInteger(System.Numerics.BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture)));

        var refused = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Contains(expected, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_yes_or_no_written_as_a_one_or_nought_with_a_point_is_read_and_anything_else_is_refused()
    {
        var bundle = await BuildAsync();
        var workflow = ((WireArray)bundle["libraries"]).Items.Cast<WireObject>().First();
        workflow.Set("enabled", new WireNumber(0.0));
        await ApplyAsync(bundle);
        Assert.Equal(0, await _store.Scalar("SELECT min(enabled) FROM libraries"));

        workflow.Set("enabled", new WireNumber(0.5));
        await Assert.ThrowsAsync<WireTypeException>(() => ApplyAsync(bundle));
    }

    [Fact]
    public async Task A_workflow_in_a_backup_with_no_name_is_refused_by_its_place_in_the_backup()
    {
        var bundle = await BuildAsync();
        var first = ((WireArray)bundle["libraries"]).Items.Cast<WireObject>().First();
        first.Remove("name");

        var refused = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.StartsWith("Workflow #1 in the backup:", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_whose_performance_section_holds_no_free_space_still_restores_every_workflow()
    {
        var bundle = await BuildAsync();
        var workflows = ((WireArray)bundle["libraries"]).Items.Count;
        var performance = (WireObject)bundle["operator_settings"];
        performance.Remove("minimum_free_disk_space_mb");
        foreach (var workflow in ((WireArray)bundle["libraries"]).Items.Cast<WireObject>())
        {
            workflow.Remove("minimum_free_disk_space_mb");
        }

        await ApplyAsync(bundle);

        Assert.Equal(workflows, await _store.Scalar("SELECT count(*) FROM libraries"));
    }

    [Fact]
    public void A_wait_written_as_a_fraction_is_read_as_a_whole_number_and_one_that_is_not_a_number_is_ignored()
    {
        var performance = new WireObject().Set("min_file_age_seconds", new WireNumber(100.9)).Set("min_input_file_size_mb", new WireNumber(double.NaN));
        var workflow = new WireObject().Set("ignore_size_changes", true).Set("hold_minutes", 0L);

        ConfigurationBundleIntakeUpgrade.Apply(performance, [workflow]);

        Assert.Equal(100L, (long)((WireInteger)workflow["ready_after_seconds"]).Value);
        Assert.Equal((long)Core.Processing.LibraryIntake.DefaultMinFileSizeMb, (long)((WireInteger)workflow["min_file_size_mb"]).Value);
    }
}
