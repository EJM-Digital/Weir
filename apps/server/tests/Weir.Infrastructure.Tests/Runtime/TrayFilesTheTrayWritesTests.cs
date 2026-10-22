using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Core.Updates;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// The files the Windows tray writes for the server (<c>pause-request.json</c>, <c>update-backup-request.json</c>, <c>update-state.json</c>,
/// <c>tray-heartbeat.json</c>, <c>lan-access</c>) are read by the server's own readers exactly as the tray wrote them
/// (<c>fixtures/tray-files</c>, which the tray's tests check against its writers).
/// </summary>
public sealed class TrayFilesTheTrayWritesTests : IDisposable
{
    private readonly StoreFixture _store = new(("WEIR_RUNTIME", "windows"));

    public void Dispose() => _store.Dispose();

    private string Home => _store.Options.WeirHome;

    [Fact]
    public async Task A_pause_the_tray_asked_for_is_applied_as_the_tray()
    {
        var changes = new DataChangePublisher();
        var pause = new SuitePauseService(new SuiteSettingsStore(new AuthStore(), changes), new ActivityStore(), changes);
        var watcher = new TrayPauseRequestWatcher(_store.Options, _store.Database, pause, _store.Clock, new CapturingLogger<TrayPauseRequestWatcher>());
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            var requestPath = Path.Join(Home, TrayPauseRequestWatcher.FileName);
            TrayFixtures.PlaceIn(Home, "pause-request.json", TrayPauseRequestWatcher.FileName);

            await Eventually.ThatAdvancingAsync(_store.Clock, TrayHandOffWatcher.Settle, () => !File.Exists(requestPath));

            var state = await _store.WithUnitOfWork(uow => pause.CurrentAsync(uow, Timestamp.UtcNow(_store.Clock)));
            Assert.True(state.Paused);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task A_request_for_a_copy_of_the_data_the_tray_made_is_answered_with_its_id()
    {
        var watcher = new TrayUpdateBackupWatcher(_store.Options, _store.Database, _store.Clock, new CapturingLogger<TrayUpdateBackupWatcher>());
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            TrayFixtures.PlaceIn(Home, "update-backup-request.json", TrayUpdateBackupWatcher.RequestFileName);
            var resultPath = Path.Join(Home, TrayUpdateBackupWatcher.ResultFileName);

            await Eventually.ThatAsync(() => File.Exists(resultPath) && File.ReadAllText(resultPath).Contains("\"state\":\"saved\"", StringComparison.Ordinal));

            var answer = WireJsonParser.Parse(File.ReadAllText(resultPath)) as WireObject;
            Assert.Equal("0123456789abcdef0123456789abcdef", (answer!["id"] as WireString)?.Value);
            Assert.Contains("1.0.0-rc.13", (answer["path"] as WireString)?.Value, StringComparison.Ordinal);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
            watcher.Dispose();
        }
    }

    [Fact]
    public void An_update_the_tray_downloaded_is_shown_as_downloaded_while_the_tray_says_it_is_alive()
    {
        TrayFixtures.PlaceIn(Home, "update-state.json", UpdateFiles.StateFileName);
        TrayFixtures.PlaceIn(Home, "tray-heartbeat.json", UpdateFiles.TrayHeartbeatFileName);
        var files = new UpdateFiles(_store.Options, _store.Clock);

        var state = files.ReadState();

        Assert.True(files.TrayIsRunning());
        Assert.False(files.TrayIsFromACopyThatCannotUpdate());
        Assert.True(files.HasDownloadedUpdate());
        Assert.Equal(UpdateStatus.StateDownloaded, (state["state"] as WireString)?.Value);
        Assert.Equal("9.9.9", (state["pending_version"] as WireString)?.Value);
        Assert.True(state["tray_running"].IsTruthy);
    }

    [Fact]
    public void The_heartbeat_of_the_tray_is_old_after_three_beats()
    {
        TrayFixtures.PlaceIn(Home, "tray-heartbeat.json", UpdateFiles.TrayHeartbeatFileName);
        var files = new UpdateFiles(_store.Options, _store.Clock);

        _store.Clock.Advance(UpdateFiles.TrayHeartbeatMaxAge);
        Assert.True(files.TrayIsRunning());

        _store.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(files.TrayIsRunning());
    }

    [Theory]
    [InlineData("lan-access-on.txt", NetworkScope.Network)]
    [InlineData("lan-access-off.txt", NetworkScope.ThisPcOnly)]
    public void The_choice_about_the_network_the_tray_saved_is_read_as_saved(string recorded, NetworkScope expected)
    {
        TrayFixtures.PlaceIn(Home, recorded, LanAccessFile.FileName);

        Assert.Equal(expected, new LanAccessFile(Home).Read());
    }

    [Theory]
    [InlineData(NetworkScope.Network, "lan-access-on.txt")]
    [InlineData(NetworkScope.ThisPcOnly, "lan-access-off.txt")]
    public void The_choice_about_the_network_the_server_saves_is_what_the_tray_wrote(NetworkScope scope, string recorded)
    {
        new LanAccessFile(Home).Write(scope);

        Assert.Equal(TrayFixtures.Text(recorded), File.ReadAllText(Path.Join(Home, LanAccessFile.FileName)));
    }
}
