using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>
/// The metadata provider key never leaves in an export. #723: restoring a configuration bundle also runs
/// every library row through the same validation a create/update request does — folder overlap and the
/// folder-safety rules, name uniqueness, and the closed enumerations — before any row is written, so a bad
/// row refuses the whole restore instead of leaving the library table partly replaced.
/// </summary>
public sealed class ConfigurationBundleStoreTests : IDisposable
{
    private readonly StoreFixture _store = new();
    private readonly ITimeZoneResolver _zones = new IanaTimeZoneResolver();
    private readonly ConfigurationBundleStore _bundle;

    public ConfigurationBundleStoreTests()
    {
        var suiteSettings = new SuiteSettingsStore(_store.Users);
        _bundle = new ConfigurationBundleStore(suiteSettings, BackupParts.Connections());
    }

    public void Dispose() => _store.Dispose();

    private Task SeedMetadataProviderKeyAsync() =>
        _store.Execute("UPDATE suite_settings SET metadata_provider = 'tmdb', metadata_provider_key_ciphertext = 'the-ciphertext' WHERE id = 1");

    private Task<WireObject> BuildBundleAsync() => _store.WithUnitOfWork(_bundle.BuildAsync, commit: false);

    private Task<bool> ApplyAsync(WireObject bundle) => _store.WithUnitOfWork(async uow =>
    {
        await _bundle.ApplyAsync(uow, bundle, _zones, _store.Options.WeirHome);
        return true;
    });

    private static WireObject LibraryAt(WireObject bundle, int index) => (WireObject)((WireArray)bundle["libraries"]).Items[index];

    [Fact]
    public async Task An_export_omits_the_metadata_provider_key_ciphertext()
    {
        await SeedMetadataProviderKeyAsync();

        var bundle = await BuildBundleAsync();
        var suiteSettings = (WireObject)bundle["suite_settings"];

        Assert.False(suiteSettings.ContainsKey("metadata_provider_key_ciphertext"));
        Assert.Equal("tmdb", ((WireString)suiteSettings["metadata_provider"]).Value);
    }

    [Fact]
    public async Task Importing_a_bundle_with_no_metadata_provider_key_keeps_the_one_already_saved()
    {
        await SeedMetadataProviderKeyAsync();
        var bundle = await BuildBundleAsync();

        await ApplyAsync(bundle);

        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM suite_settings WHERE id = 1 AND metadata_provider_key_ciphertext = 'the-ciphertext'"));
    }

    [Fact]
    public async Task A_watched_folder_with_no_output_folder_refuses_the_whole_restore()
    {
        var bundle = await BuildBundleAsync();
        LibraryAt(bundle, 0).Set("watched_folder", new WireString(@"C:\media\watched"));

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Contains("Movies", exception.Message, StringComparison.Ordinal);
        Assert.Contains("output folder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_folders_between_two_restored_libraries_refuse_the_whole_restore()
    {
        var bundle = await BuildBundleAsync();
        LibraryAt(bundle, 0).Set("watched_folder", new WireString(@"C:\media\shared\in"));
        LibraryAt(bundle, 0).Set("output_folder", new WireString(@"C:\media\shared\out"));
        LibraryAt(bundle, 1).Set("watched_folder", new WireString(@"C:\media\shared\out\nested"));
        LibraryAt(bundle, 1).Set("output_folder", new WireString(@"C:\media\tv-out"));

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Contains("overlap", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_rejected_file_action_refuses_the_whole_restore()
    {
        var bundle = await BuildBundleAsync();
        LibraryAt(bundle, 0).Set("rejected_file_action", new WireString("quarantine"));

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Contains("rejected file action", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_folder_inside_weirs_own_data_folder_refuses_the_whole_restore()
    {
        var bundle = await BuildBundleAsync();
        var insideWeirHome = Path.Combine(_store.Options.WeirHome, "watched");
        LibraryAt(bundle, 0).Set("watched_folder", new WireString(insideWeirHome));
        LibraryAt(bundle, 0).Set("output_folder", new WireString(Path.Combine(_store.Options.WeirHome, "output")));

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Contains("data folder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duplicate_library_name_refuses_the_whole_restore()
    {
        var bundle = await BuildBundleAsync();
        LibraryAt(bundle, 1).Set("name", new WireString("Movies"));

        var exception = await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_row_leaves_the_library_table_completely_untouched()
    {
        var bundle = await BuildBundleAsync();
        LibraryAt(bundle, 0).Set("watched_folder", new WireString(@"C:\media\watched"));

        await Assert.ThrowsAsync<WireValueException>(() => ApplyAsync(bundle));

        var names = await _store.WithUnitOfWork(
            uow => uow.QueryAsync("SELECT name FROM libraries ORDER BY id", reader => reader.GetString(0)),
            commit: false);
        Assert.Equal(["Movies", "TV"], names);
    }

    [Fact]
    public async Task A_valid_bundle_restores_without_complaint()
    {
        var bundle = await BuildBundleAsync();
        LibraryAt(bundle, 0).Set("watched_folder", new WireString(@"C:\media\movies-in"));
        LibraryAt(bundle, 0).Set("output_folder", new WireString(@"C:\media\movies-out"));

        await ApplyAsync(bundle);

        var watched = await _store.WithUnitOfWork(
            uow => uow.QueryAsync("SELECT watched_folder FROM libraries WHERE name = 'Movies'", reader => reader.GetString(0)),
            commit: false);
        Assert.Equal(@"C:\media\movies-in", Assert.Single(watched));
    }

    [Fact]
    public async Task An_export_carries_the_workflows_one_wait_and_minimum_size_and_none_of_the_retired_settings()
    {
        var bundle = await BuildBundleAsync();

        var workflow = LibraryAt(bundle, 0);
        var performance = (WireObject)bundle["operator_settings"];
        Assert.Equal((60, 50), (Number(workflow, "ready_after_seconds"), Number(workflow, "min_file_size_mb")));
        foreach (var retired in new[] { "min_file_age_seconds", "hold_minutes", "file_detection_interval_seconds" })
        {
            Assert.False(workflow.ContainsKey(retired), retired);
        }

        Assert.False(performance.ContainsKey("min_file_age_seconds"));
        Assert.False(performance.ContainsKey("min_input_file_size_mb"));
    }

    [Fact]
    public async Task A_current_bundle_restores_each_workflows_wait_and_minimum_size_as_they_are()
    {
        var bundle = await BuildBundleAsync();
        LibraryAt(bundle, 0).Set("ready_after_seconds", 5L).Set("min_file_size_mb", 0L);
        LibraryAt(bundle, 1).Set("ready_after_seconds", 900L).Set("min_file_size_mb", 200L);

        await ApplyAsync(bundle);

        Assert.Equal(5, await _store.Scalar("SELECT ready_after_seconds FROM libraries WHERE name = 'Movies'"));
        Assert.Equal(0, await _store.Scalar("SELECT min_file_size_mb FROM libraries WHERE name = 'Movies'"));
        Assert.Equal(900, await _store.Scalar("SELECT ready_after_seconds FROM libraries WHERE name = 'TV'"));
        Assert.Equal(200, await _store.Scalar("SELECT min_file_size_mb FROM libraries WHERE name = 'TV'"));
    }

    [Fact]
    public async Task An_older_bundle_restores_each_workflow_with_the_longest_wait_it_had_and_the_minimum_size_it_used()
    {
        var bundle = await BuildBundleAsync();
        ((WireObject)bundle["operator_settings"]).Set("min_file_age_seconds", 100L).Set("min_input_file_size_mb", 75L);
        var followsPerformance = LibraryAt(bundle, 0);
        followsPerformance.Remove("ready_after_seconds");
        followsPerformance.Set("min_file_age_seconds", WireNull.Instance).Set("hold_minutes", 2L).Set("file_detection_interval_seconds", 30L)
            .Set("min_file_size_mb", WireNull.Instance);
        var ownValues = LibraryAt(bundle, 1);
        ownValues.Remove("ready_after_seconds");
        ownValues.Set("min_file_age_seconds", 10L).Set("hold_minutes", 0L).Set("file_detection_interval_seconds", 90L).Set("min_file_size_mb", 200L);

        await ApplyAsync(bundle);

        Assert.Equal(220, await _store.Scalar("SELECT ready_after_seconds FROM libraries WHERE name = 'Movies'"));
        Assert.Equal(75, await _store.Scalar("SELECT min_file_size_mb FROM libraries WHERE name = 'Movies'"));
        Assert.Equal(90, await _store.Scalar("SELECT ready_after_seconds FROM libraries WHERE name = 'TV'"));
        Assert.Equal(200, await _store.Scalar("SELECT min_file_size_mb FROM libraries WHERE name = 'TV'"));
    }

    private static long Number(WireObject row, string key) => (long)((WireInteger)row[key]).Value;
}
