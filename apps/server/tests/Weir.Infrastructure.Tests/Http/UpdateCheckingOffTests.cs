using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.Updates;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Http;

/// <summary>
/// The Windows app starts with update checking off, and while it is off nothing is asked of GitHub. A Docker install has no tray to
/// hold the choice, so it keeps looking.
/// </summary>
public sealed class UpdateCheckingOffTests : IDisposable
{
    private readonly List<StoreFixture> _fixtures = [];
    private readonly CountingCatalog _catalog = new();

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
        {
            fixture.Dispose();
        }
    }

    private (UpdateStatusReader Reader, UpdateOutlook Outlook, UpdateFiles Files, StoreFixture Fixture) Install(string runtime)
    {
        var fixture = new StoreFixture(("WEIR_VERSION", "3.2.16"), ("WEIR_RUNTIME", runtime));
        _fixtures.Add(fixture);
        var files = new UpdateFiles(fixture.Options);
        var reader = new UpdateStatusReader(_catalog, fixture.Options, fixture.Database, new SuiteSettingsStore(fixture.Users), files, NullLogger<UpdateStatusReader>.Instance);
        return (reader, new UpdateOutlook(reader, files, fixture.Clock, new DataChangePublisher()), files, fixture);
    }

    private static string TextOf(WireObject status, string key) => ((WireString)status[key]).Value;

    [Fact]
    public async Task A_new_Windows_install_asks_GitHub_nothing_and_says_checking_is_off()
    {
        var (reader, _, _, _) = Install("windows");

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("off", TextOf(status, "status"));
        Assert.Equal("windows", TextOf(status, "install_type"));
        Assert.Equal("Update checking is off. Turn it on below to look for new versions of Weir.", TextOf(status, "summary"));
        Assert.Equal(0, _catalog.Calls);
    }

    [Fact]
    public async Task A_Windows_install_that_turned_checking_on_asks_GitHub()
    {
        var (reader, _, files, _) = Install("windows");
        files.WriteSettings("Auto", true, 60, checkForUpdates: true);

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("up_to_date", TextOf(status, "status"));
        Assert.Equal(1, _catalog.Calls);
    }

    [Fact]
    public async Task A_Windows_install_whose_file_was_saved_before_the_choice_existed_keeps_checking()
    {
        var (reader, _, _, fixture) = Install("windows");
        File.WriteAllText(Path.Join(fixture.Home.Path, UpdateFiles.SettingsFileName), "{\"mode\": \"NotifyOnly\", \"checkOnStartup\": true, \"checkIntervalMinutes\": 240}");

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("up_to_date", TextOf(status, "status"));
        Assert.Equal(1, _catalog.Calls);
    }

    [Fact]
    public async Task A_Windows_install_that_switched_checking_off_asks_GitHub_nothing()
    {
        var (reader, _, files, _) = Install("windows");
        files.WriteSettings("Auto", true, 60, checkForUpdates: false);

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("off", TextOf(status, "status"));
        Assert.Equal(0, _catalog.Calls);
    }

    [Fact]
    public async Task An_unreadable_settings_file_asks_GitHub_nothing()
    {
        var (reader, _, _, fixture) = Install("windows");
        File.WriteAllText(Path.Join(fixture.Home.Path, UpdateFiles.SettingsFileName), "{\"mode\": \"Notify");

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("off", TextOf(status, "status"));
        Assert.Equal(0, _catalog.Calls);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("source")]
    public async Task A_Docker_or_source_install_has_no_choice_to_hold_and_keeps_looking(string runtime)
    {
        var (reader, _, _, _) = Install(runtime);

        var status = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("up_to_date", TextOf(status, "status"));
        Assert.Equal(1, _catalog.Calls);
    }

    [Fact]
    public async Task Switching_checking_on_shows_in_the_outlook_at_once()
    {
        var (_, outlook, files, _) = Install("windows");
        await outlook.CheckAsync(CancellationToken.None);
        Assert.Equal(new UpdateOutlookSnapshot("off", null), outlook.Current());

        files.WriteSettings("Auto", true, 60, checkForUpdates: true);

        await Eventually.ThatAsync(() => outlook.Current().Status == "up_to_date");
        Assert.Equal(1, _catalog.Calls);
    }

    private sealed class CountingCatalog : IReleaseCatalogClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<GitHubReleaseRecord?> FetchLatestAsync(string currentVersion, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<GitHubReleaseRecord?>(new GitHubReleaseRecord("v3.2.16", "3.2.16", null, null, null, false, false, []));
        }
    }
}
