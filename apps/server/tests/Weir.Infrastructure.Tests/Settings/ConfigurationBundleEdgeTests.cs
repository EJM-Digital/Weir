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
