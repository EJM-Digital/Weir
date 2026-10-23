using Microsoft.Extensions.Logging;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The tray's files are looked for in Weir's own folder; when that folder cannot be watched, Weir says so once and the watcher has nothing to do.</summary>
public sealed class TrayWatchersWithoutAFolderTests : IDisposable
{
    private readonly StoreFixture _store = new();

    public void Dispose() => _store.Dispose();

    private Weir.Core.Configuration.WeirOptions Missing() =>
        _store.Options with { WeirHome = _store.Home.Join("not-there") };

    [Fact]
    public async Task Pause_and_resume_in_the_tray_are_said_not_to_work_when_the_folder_cannot_be_watched()
    {
        var log = new CapturingLogger<TrayPauseRequestWatcher>();
        var changes = new DataChangePublisher();
        var pause = new SuitePauseService(new SuiteSettingsStore(new AuthStore(), changes), new ActivityStore(), changes);
        using var watcher = new TrayPauseRequestWatcher(Missing(), _store.Database, pause, _store.Clock, log);

        await watcher.StartAsync(CancellationToken.None);
        await watcher.ExecuteTask!;

        Assert.True(log.Logged(LogLevel.Warning, "Pause and Resume in the tray will not work until Weir is restarted"));
        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_tray_answers_are_said_to_wait_for_the_about_page_when_the_folder_cannot_be_watched()
    {
        var log = new CapturingLogger<TrayHandOffWatcher>();
        using var watcher = new TrayHandOffWatcher(Missing(), new DataChangePublisher(), _store.Clock, log);

        await watcher.StartAsync(CancellationToken.None);
        await watcher.ExecuteTask!;

        Assert.True(log.Logged(LogLevel.Warning, "System › About shows them when it is next opened"));
        await watcher.StopAsync(CancellationToken.None);
    }
}
