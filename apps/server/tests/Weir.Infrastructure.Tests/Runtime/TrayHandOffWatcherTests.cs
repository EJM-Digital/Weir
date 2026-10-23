using Microsoft.Extensions.Logging.Abstractions;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>The tray's answers reach the open screens when the tray writes them: a downloaded update, and the firewall prompt's outcome.</summary>
public sealed class TrayHandOffWatcherTests : IAsyncLifetime, IDisposable
{
    /// <summary>The longest a wait for something the test has already arranged may take before the test calls it hung.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    private readonly StoreFixture _store = new();
    private readonly DataChangePublisher _changes = new();
    private TimerWatchingClock? _clock;
    private TrayHandOffWatcher? _watcher;

    private string Home => _store.Options.WeirHome;

    public async Task InitializeAsync()
    {
        _clock = new TimerWatchingClock(_store.Clock);
        _watcher = new TrayHandOffWatcher(_store.Options, _changes, _clock, NullLogger<TrayHandOffWatcher>.Instance);
        await _watcher.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _watcher!.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _store.Dispose();
    }

    /// <summary>The next topic announced, moving the clock past the watcher's settle time until it comes.</summary>
    private async Task<string> NextAsync(BroadcastSubscription<string> heard)
    {
        var next = ReadOneAsync(heard);
        await Eventually.ThatAdvancingAsync(_store.Clock, TrayHandOffWatcher.Settle, () => next.IsCompleted);
        return await next;
    }

    private static async Task<string> ReadOneAsync(BroadcastSubscription<string> heard)
    {
        await foreach (var topic in heard.ReadAllAsync(CancellationToken.None))
        {
            return topic;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    /// <summary>Everything announced up to and including <paramref name="last"/>, which the test arranges to be announced after the rest.</summary>
    private async Task<string[]> HeardUntilAsync(BroadcastSubscription<string> heard, string last)
    {
        var topics = new List<string>();
        do
        {
            topics.Add(await NextAsync(heard));
        }
        while (topics[^1] != last);

        return [.. topics];
    }

    /// <summary>Makes the watcher hear <paramref name="fileName"/> and waits until it is holding that write back for the settle time.</summary>
    private async Task HearAndWaitForSettleAsync(string fileName)
    {
        var timers = _clock!.TimersSet;
        _watcher!.Hear(fileName);
        await Eventually.ThatAsync(() => _clock.TimersSet > timers);
    }

    [Fact]
    public async Task An_update_the_tray_downloaded_is_announced()
    {
        using var heard = _changes.Subscribe();

        await File.WriteAllTextAsync(Path.Join(Home, UpdateFiles.StateFileName), "{\"downloaded\": true, \"version\": \"9.9.9\"}");

        Assert.Equal(DataTopics.Update, await NextAsync(heard));
    }

    [Fact]
    public async Task An_update_the_tray_held_back_for_want_of_a_copy_of_the_data_is_announced()
    {
        using var heard = _changes.Subscribe();

        await File.WriteAllTextAsync(Path.Join(Home, UpdateFiles.NotAppliedFileName), "{\"version\": \"9.9.9\", \"reason\": \"The drive is full.\"}");

        Assert.Equal(DataTopics.Update, await NextAsync(heard));
    }

    [Fact]
    public async Task A_choice_saved_for_the_tray_is_announced_even_when_it_is_the_same_choice()
    {
        var choice = new LanAccessFile(Home);
        choice.Write(NetworkScope.Network);
        using var heard = _changes.Subscribe();

        choice.Write(NetworkScope.Network);

        Assert.Equal(DataTopics.NetworkAccess, await NextAsync(heard));
    }

    [Fact]
    public async Task Writes_that_follow_each_other_closer_than_the_settle_time_are_announced_together()
    {
        using var heard = _changes.Subscribe();

        // Twelve writes, each heard before the settle time of the one before has passed, that together last longer than it.
        await HearAndWaitForSettleAsync(UpdateFiles.StateFileName);
        for (var write = 1; write < 12; write++)
        {
            _watcher!.Hear(UpdateFiles.StateFileName);
            var timers = _clock!.TimersSet;
            _store.Clock.Advance(TrayHandOffWatcher.Settle);
            await Eventually.ThatAsync(() => _clock.TimersSet > timers);
        }

        _store.Clock.Advance(TrayHandOffWatcher.Settle);
        Assert.Equal(DataTopics.Update, await ReadOneAsync(heard).WaitAsync(HangGuard));

        // The next thing announced is a later write to another file, so nothing was announced in between.
        _watcher!.Hear(LanAccessFile.FileName);
        Assert.Equal(DataTopics.NetworkAccess, await NextAsync(heard));
    }

    [Fact]
    public async Task A_burst_that_never_goes_quiet_is_still_announced_every_most_waited()
    {
        using var heard = _changes.Subscribe();

        await HearAndWaitForSettleAsync(UpdateFiles.StateFileName);
        var announced = ReadOneAsync(heard);
        for (var held = TimeSpan.Zero; held < TrayHandOffWatcher.MostWaited; held += TrayHandOffWatcher.Settle)
        {
            Assert.False(announced.IsCompleted);
            _watcher!.Hear(UpdateFiles.StateFileName);
            var timers = _clock!.TimersSet;
            _store.Clock.Advance(TrayHandOffWatcher.Settle);
            await Eventually.ThatAsync(() => announced.IsCompleted || _clock.TimersSet > timers);
        }

        Assert.Equal(DataTopics.Update, await announced.WaitAsync(HangGuard));
    }

    [Fact]
    public async Task Each_file_announces_its_own_data()
    {
        using var heard = _changes.Subscribe();

        await File.WriteAllTextAsync(Path.Join(Home, UpdateFiles.StateFileName), "{\"downloaded\": false}");
        await File.WriteAllTextAsync(Path.Join(Home, LanAccessFile.FileName), "on");

        var topics = new HashSet<string>();
        while (topics.Count < 2)
        {
            topics.Add(await NextAsync(heard));
        }

        Assert.Equal([DataTopics.NetworkAccess, DataTopics.Update], topics.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Files_the_tray_does_not_answer_through_are_not_announced()
    {
        using var heard = _changes.Subscribe();

        await File.WriteAllTextAsync(Path.Join(Home, UpdateFiles.WorkStateFileName), "{}");
        await File.WriteAllTextAsync(Path.Join(Home, "notes.txt"), "not a hand-off");
        await File.WriteAllTextAsync(Path.Join(Home, LanAccessFile.FileName), "on");

        Assert.Equal([DataTopics.NetworkAccess], await HeardUntilAsync(heard, DataTopics.NetworkAccess));
    }

    [Fact]
    public async Task Events_lost_to_a_full_buffer_announce_every_topic_and_a_write_without_a_name_announces_none()
    {
        using var heard = _changes.Subscribe();

        _watcher!.Hear(null);
        _watcher.HearOverflow();

        Assert.Equal([DataTopics.Update, DataTopics.NetworkAccess], await HeardUntilAsync(heard, DataTopics.NetworkAccess));
    }

    [Fact]
    public async Task A_file_written_whole_and_renamed_into_place_is_announced_by_its_own_name()
    {
        using var heard = _changes.Subscribe();

        File.WriteAllText(Path.Join(Home, ".update-state.json.1a2b3c4d.tmp"), "{\"downloaded\": true, \"version\": \"9.9.9\"}");
        File.Move(Path.Join(Home, ".update-state.json.1a2b3c4d.tmp"), Path.Join(Home, UpdateFiles.StateFileName), overwrite: true);

        Assert.Equal(DataTopics.Update, await NextAsync(heard));
    }
}
