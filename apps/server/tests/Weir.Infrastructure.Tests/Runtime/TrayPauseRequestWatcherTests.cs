using Microsoft.Extensions.Logging;
using Weir.Core.Settings;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>Pause and Resume in the tray reach the server through <c>pause-request.json</c>: they are applied as the tray, recorded in Activity, and the request is taken away once it has been answered.</summary>
public sealed class TrayPauseRequestWatcherTests : IDisposable
{
    private const string Entries =
        "SELECT group_concat(title || ': ' || detail, ' | ') FROM " +
        "(SELECT title, detail FROM activity_events WHERE event_type LIKE 'system.processing_%' ORDER BY id)";

    private readonly StoreFixture _store = new();
    private readonly SuitePauseService _pause;
    private readonly CapturingLogger<TrayPauseRequestWatcher> _log = new();
    private readonly TrayPauseRequestWatcher _watcher;

    public TrayPauseRequestWatcherTests()
    {
        var changes = new DataChangePublisher();
        _pause = new SuitePauseService(new SuiteSettingsStore(new AuthStore(), changes), new ActivityStore(), changes);
        _watcher = new TrayPauseRequestWatcher(_store.Options, _store.Database, _pause, _store.Clock, _log);
    }

    private string RequestPath => Path.Join(_store.Options.WeirHome, TrayPauseRequestWatcher.FileName);

    public void Dispose()
    {
        _watcher.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _watcher.Dispose();
        _store.Dispose();
    }

    private Task<PauseState> CurrentAsync() =>
        _store.WithUnitOfWork(uow => _pause.CurrentAsync(uow, Timestamp.UtcNow(_store.Clock)));

    private Task<PauseState> ChangeAsync(bool paused, bool keepLooking = true) =>
        _store.WithUnitOfWork(uow => _pause.ChangeAsync(uow, paused, null, keepEnd: false, keepLooking, Timestamp.UtcNow(_store.Clock), "alice"));

    private async Task<string[]> EntriesAsync()
    {
        using var connection = _store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Entries;
        return (await command.ExecuteScalarAsync() as string)?.Split(" | ") ?? [];
    }

    private static string Request(bool paused, string requestedAt = "2026-01-15T10:00:00Z") =>
        $"{{\"paused\": {(paused ? "true" : "false")}, \"requested_at\": \"{requestedAt}\"}}";

    /// <summary>Moves the clock past the watcher's pauses until the request is gone, which it is once the watcher has answered it.</summary>
    private Task UntilTheRequestIsTakenAwayAsync() => UntilAsync(() => !File.Exists(RequestPath));

    private Task UntilAsync(Func<bool> condition) =>
        Eventually.ThatAdvancingAsync(_store.Clock, TrayHandOffWatcher.Settle, condition);

    /// <summary>Writes the request and waits for the server to take it away, which it does once it has answered.</summary>
    private async Task RequestAsync(string contents)
    {
        await File.WriteAllTextAsync(RequestPath, contents);
        await UntilTheRequestIsTakenAwayAsync();
    }

    [Fact]
    public async Task A_pause_asked_for_by_the_tray_lasts_until_resumed_and_is_recorded()
    {
        await _watcher.StartAsync(CancellationToken.None);

        await RequestAsync(Request(paused: true));

        var state = await CurrentAsync();
        Assert.True(state.Paused);
        Assert.Null(state.PausedUntil);
        Assert.Equal(["Processing paused: The tray paused processing until you resume it. Weir keeps looking for new files and starts nothing."], await EntriesAsync());
    }

    [WindowsFact("Only Windows refuses to delete a file another program holds open without sharing delete.")]
    public async Task A_request_the_server_cannot_remove_stays_answered_and_the_failure_is_logged()
    {
        await File.WriteAllTextAsync(RequestPath, Request(paused: true));
        using var held = new FileStream(RequestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        await _watcher.StartAsync(CancellationToken.None);
        await UntilAsync(() => _log.Logged(LogLevel.Warning, "could not remove the tray's pause request"));

        Assert.True((await CurrentAsync()).Paused);
        Assert.True(File.Exists(RequestPath));
    }

    [Fact]
    public async Task A_resume_asked_for_by_the_tray_is_recorded()
    {
        await ChangeAsync(paused: true);
        await _watcher.StartAsync(CancellationToken.None);

        await RequestAsync(Request(paused: false));

        Assert.False((await CurrentAsync()).Paused);
        Assert.Equal("Processing resumed: The tray resumed processing.", (await EntriesAsync())[^1]);
    }

    [Fact]
    public async Task A_pause_from_the_tray_leaves_the_choice_to_keep_looking_for_new_files_as_it_stands()
    {
        await ChangeAsync(paused: false, keepLooking: false);
        await _watcher.StartAsync(CancellationToken.None);

        await RequestAsync(Request(paused: true));

        var state = await CurrentAsync();
        Assert.True(state.Paused);
        Assert.False(state.ScanWhilePaused);
    }

    [Fact]
    public async Task A_fresh_request_left_while_the_server_was_down_is_answered_when_it_starts()
    {
        await File.WriteAllTextAsync(RequestPath, Request(paused: true));

        await _watcher.StartAsync(CancellationToken.None);

        await UntilTheRequestIsTakenAwayAsync();
        Assert.True((await CurrentAsync()).Paused);
    }

    [Fact]
    public async Task An_old_request_left_while_the_server_was_down_is_dropped_and_said_so()
    {
        await File.WriteAllTextAsync(RequestPath, Request(paused: true, requestedAt: "2026-01-15T09:57:00Z"));

        await _watcher.StartAsync(CancellationToken.None);

        await UntilTheRequestIsTakenAwayAsync();
        Assert.False((await CurrentAsync()).Paused);
        Assert.Empty(await EntriesAsync());
        Assert.True(_log.Logged(LogLevel.Information, "made more than 2 minutes ago"));
    }

    [Fact]
    public async Task A_request_is_answered_once_even_when_its_file_turns_up_again()
    {
        await _watcher.StartAsync(CancellationToken.None);
        await RequestAsync(Request(paused: true));
        await ChangeAsync(paused: false);

        await RequestAsync(Request(paused: true));

        Assert.False((await CurrentAsync()).Paused);
        Assert.Equal(2, (await EntriesAsync()).Length);
    }

    [Fact]
    public async Task A_request_that_cannot_be_applied_stays_and_is_applied_when_the_server_can()
    {
        await ChangeAsync(paused: false);
        await _store.Execute("UPDATE suite_settings SET processing_paused_until = 'not a time' WHERE id = 1");
        await _watcher.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(RequestPath, Request(paused: true));
        await UntilAsync(() => _log.Logged(LogLevel.Warning, "could not answer the pause request"));
        Assert.True(File.Exists(RequestPath));

        await _store.Execute("UPDATE suite_settings SET processing_paused_until = NULL WHERE id = 1");
        await UntilTheRequestIsTakenAwayAsync();
        Assert.True((await CurrentAsync()).Paused);
    }

    [Fact]
    public async Task A_request_still_being_written_is_read_again_before_it_counts_as_bad()
    {
        await _watcher.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(RequestPath, "{\"paused\": tr");
        await UntilAsync(() => _log.Logged(LogLevel.Debug, "cannot be read yet"));
        await File.WriteAllTextAsync(RequestPath, Request(paused: true));

        await UntilTheRequestIsTakenAwayAsync();
        Assert.True((await CurrentAsync()).Paused);
    }

    /// <summary>
    /// The tray writes a second request while the first is being applied (held up here by a write in progress elsewhere). The second
    /// must not be deleted with the first: whichever way the two interleave, the last word is the tray's.
    /// </summary>
    [Fact]
    public async Task A_request_the_tray_rewrites_while_the_last_is_applied_is_not_lost()
    {
        await ChangeAsync(paused: false);
        await _watcher.StartAsync(CancellationToken.None);
        var holding = await UnitOfWork.OpenAsync(_store.Database);
        await using (holding)
        {
            await holding.ExecuteAsync("UPDATE suite_settings SET app_timezone = app_timezone WHERE id = 1");
            await File.WriteAllTextAsync(RequestPath, Request(paused: true, requestedAt: "2026-01-15T09:59:00Z"));
            await UntilAsync(() => _watcher.RequestsRead >= 1);
            await File.WriteAllTextAsync(RequestPath, Request(paused: false, requestedAt: "2026-01-15T09:59:30Z"));
            await holding.CommitAsync();
        }

        await UntilTheRequestIsTakenAwayAsync();

        Assert.False((await CurrentAsync()).Paused);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[true]")]
    [InlineData("{\"requested_at\": \"2026-01-15T10:00:00Z\"}")]
    [InlineData("{\"paused\": true}")]
    [InlineData("{\"paused\": \"yes\", \"requested_at\": \"2026-01-15T10:00:00Z\"}")]
    [InlineData("{\"paused\": true, \"requested_at\": \"yesterday-ish\"}")]
    public async Task A_request_that_cannot_be_read_is_ignored_and_removed(string contents)
    {
        await _watcher.StartAsync(CancellationToken.None);

        await RequestAsync(contents);

        Assert.False((await CurrentAsync()).Paused);
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task A_request_that_is_refused_for_a_moment_is_looked_at_again_and_not_lost()
    {
        await File.WriteAllTextAsync(RequestPath, Request(paused: true));
        var held = new FileStream(RequestPath, FileMode.Open, FileAccess.Read, FileShare.None);
        await _watcher.StartAsync(CancellationToken.None);

        await UntilAsync(() => _log.Logged(LogLevel.Debug, "refused for a moment"));
        Assert.False((await CurrentAsync()).Paused);
        Assert.True(File.Exists(RequestPath));

        held.Dispose();
        await UntilTheRequestIsTakenAwayAsync();
        Assert.True((await CurrentAsync()).Paused);
    }

    [WindowsFact("Only Windows refuses a read of a file another program holds without sharing it.")]
    public async Task A_request_that_is_refused_on_the_second_look_is_looked_at_again_and_not_lost()
    {
        await _watcher.StartAsync(CancellationToken.None);
        await File.WriteAllTextAsync(RequestPath, "{\"paused\": tr");
        await Eventually.ThatAsync(() =>
        {
            var waiting = _log.Logged(LogLevel.Debug, "cannot be read yet");
            if (!waiting)
            {
                _store.Clock.Advance(TrayHandOffWatcher.Settle);
            }

            return waiting;
        });

        using (new FileStream(RequestPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await UntilAsync(() => _log.Logged(LogLevel.Debug, "refused for a moment"));
            Assert.False((await CurrentAsync()).Paused);
        }

        await File.WriteAllTextAsync(RequestPath, Request(paused: true));
        await UntilTheRequestIsTakenAwayAsync();
        Assert.True((await CurrentAsync()).Paused);
    }
}
