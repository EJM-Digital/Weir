using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>The tray hears of a new tray-status.json when the server writes it, with no timer to wait for.</summary>
public sealed class TrayStatusWatcherTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuickRetry = TimeSpan.FromMilliseconds(20);

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly FakeTimeProvider _clock = new();
    private readonly BlockingCollection<TrayStatusReading> _heard = [];
    private readonly BlockingCollection<string> _logged = [];

    public void Dispose()
    {
        _heard.Dispose();
        _logged.Dispose();
        _home.Dispose();
    }

    private TrayStatusReading NextHeard() =>
        _heard.TryTake(out var reading, Ceiling) ? reading : throw new TimeoutException("The watcher never reported.");

    private string NextLogged() =>
        _logged.TryTake(out var line, Ceiling) ? line : throw new TimeoutException("The watcher never logged.");

    private TrayStatusWatcher StartWatching()
    {
        var watcher = new TrayStatusWatcher(_home.Path, _heard.Add);
        watcher.Start();
        return watcher;
    }

    // A watcher whose reading the test controls: it answers from a queue, then keeps giving the last answer. Its settle and
    // retry timers are on the test's clock, so nothing happens until the test moves the clock on.
    private TrayStatusWatcher WatchingReadings(params TrayStatusReading[] readings)
    {
        var queue = new Queue<TrayStatusReading>(readings);
        TrayStatusReading last = default;
        var watcher = new TrayStatusWatcher(
            () =>
            {
                lock (queue)
                {
                    last = queue.Count > 0 ? queue.Dequeue() : last;
                    return last;
                }
            },
            _heard.Add,
            _logged.Add,
            _clock,
            QuickRetry);
        watcher.Start();
        return watcher;
    }

    private static TrayStatusReading Reading(bool paused) =>
        new(new TrayStatus(paused, null, [], [], true), DateTime.UtcNow, Failed: false);

    [Fact]
    public void Starting_reports_a_file_that_is_already_there()
    {
        File.WriteAllText(Path.Combine(_home.Path, TrayStatusFile.FileName), """{ "paused": true }""");

        using var watcher = StartWatching();

        Assert.True(NextHeard().Status!.Paused);
    }

    [Fact]
    public void Starting_with_no_file_reports_no_status()
    {
        using var watcher = StartWatching();

        Assert.Null(NextHeard().Status);
    }

    [Fact]
    public void A_status_the_server_writes_whole_and_renames_into_place_is_reported_when_it_lands()
    {
        using var watcher = StartWatching();
        Assert.Null(NextHeard().Status);

        AtomicFile.WriteAllText(_home.Path, TrayStatusFile.FileName, """{ "paused": true, "unreachable": { "managers": ["Deluno"] } }""");

        var latest = NextHeard();
        while (latest.Status is null)
        {
            latest = NextHeard();
        }
        Assert.True(latest.Status.Paused);
        Assert.Equal(["Deluno"], latest.Status.ManagersUnreachable);
    }

    [Fact]
    public void A_burst_of_writes_ends_in_the_last_status()
    {
        using var watcher = StartWatching();
        Assert.Null(NextHeard().Status);

        for (var managers = 1; managers <= 5; managers++)
        {
            var names = string.Join(", ", Enumerable.Range(1, managers).Select(n => $"\"M{n}\""));
            AtomicFile.WriteAllText(_home.Path, TrayStatusFile.FileName, $$"""{ "unreachable": { "managers": [{{names}}] } }""");
        }

        var last = NextHeard();
        while (last.Status is not { ManagersUnreachable.Count: 5 })
        {
            last = NextHeard();
        }
        Assert.Equal(5, last.Status.ManagersUnreachable.Count);
    }

    [Fact]
    public void A_file_the_server_removes_is_reported_as_no_status()
    {
        File.WriteAllText(Path.Combine(_home.Path, TrayStatusFile.FileName), "{}");
        using var watcher = StartWatching();
        Assert.NotNull(NextHeard().Status);

        File.Delete(Path.Combine(_home.Path, TrayStatusFile.FileName));

        Assert.Null(NextHeard().Status);
    }

    [Fact]
    public void A_read_that_fails_is_tried_once_more_and_the_second_answer_is_reported()
    {
        using var watcher = WatchingReadings(TrayStatusReading.Unreadable, Reading(paused: true));
        Assert.Empty(_heard);

        _clock.Advance(QuickRetry);

        Assert.True(NextHeard().Status!.Paused);
    }

    [Fact]
    public void A_file_that_stays_unreadable_reports_nothing_so_the_status_shown_stands()
    {
        using var watcher = WatchingReadings(Reading(paused: true), TrayStatusReading.Unreadable);
        Assert.True(NextHeard().Status!.Paused);

        watcher.OnWatcherError(new IOException("changed"));
        _clock.Advance(TrayStatusWatcher.Settle);
        Assert.Empty(_heard);
        _clock.Advance(QuickRetry);

        Assert.Contains("keeps the status it has", NextLoggedAbout("still could not be read"), StringComparison.Ordinal);
        Assert.Empty(_heard);
    }

    // Skips the log lines about the watcher's own error to reach the one about the file.
    private string NextLoggedAbout(string fragment)
    {
        while (true)
        {
            var line = NextLogged();
            if (line.Contains(fragment, StringComparison.Ordinal))
            {
                return line;
            }
        }
    }

    [Fact]
    public void A_buffer_overflow_is_logged_once_a_minute_and_each_time_the_file_is_read_again()
    {
        var clock = new FakeTimeProvider();
        using var watcher = new TrayStatusWatcher(
            () => TrayStatusReading.Missing,
            _heard.Add,
            _logged.Add,
            clock,
            QuickRetry);

        watcher.OnWatcherError(new InternalBufferOverflowException("too many changes"));
        watcher.OnWatcherError(new InternalBufferOverflowException("too many changes"));
        clock.Advance(TimeSpan.FromSeconds(59));
        watcher.OnWatcherError(new InternalBufferOverflowException("too many changes"));

        Assert.Single(_logged);
        Assert.Contains("too many changes", NextLogged(), StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(2));
        watcher.OnWatcherError(new InternalBufferOverflowException("too many changes"));
        Assert.Contains("too many changes", NextLogged(), StringComparison.Ordinal);

        // Each error also has the file read again, once the burst has settled.
        clock.Advance(TrayStatusWatcher.Settle);
        Assert.False(NextHeard().Failed);
    }

    [Fact]
    public void The_watcher_has_a_buffer_of_64_KB_so_a_burst_of_writes_does_not_overflow_it()
    {
        using var watcher = new TrayStatusWatcher(_home.Path, _heard.Add);

        Assert.Equal(64 * 1024, watcher.BufferBytes);
    }
}
