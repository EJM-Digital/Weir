namespace Weir.Tray;

/// <summary>
/// Tells the tray when the server rewrites tray-status.json, the moment it does and with no timer. The server writes it
/// whole to a scratch file and renames it into place, which reaches a file watcher as a burst of events; the burst is
/// read once, after it has settled. A file that cannot be read is tried once more shortly after, and if it still cannot
/// be read nothing is reported: the status the tray already shows stands.
/// </summary>
sealed class TrayStatusWatcher : IDisposable
{
    /// <summary>How long a burst of events is given to finish before the file is read.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    /// <summary>How long after a read that failed the file is read once more.</summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromMilliseconds(200);

    /// <summary>The least time between two log lines about the watcher itself failing.</summary>
    internal static readonly TimeSpan ErrorLogInterval = TimeSpan.FromMinutes(1);

    // Large enough that a burst of the server's writes does not overflow the watcher's buffer.
    private const int WatcherBufferBytes = 64 * 1024;

    private readonly Func<TrayStatusReading> _read;
    private readonly Action<TrayStatusReading> _changed;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retryAfter;
    private readonly FileSystemWatcher? _watcher;
    private readonly ITimer _settle;
    private bool _retried;
    private long? _lastErrorLogged;

    /// <param name="runtimeHome">The folder holding tray-status.json.</param>
    /// <param name="changed">Called, on a thread-pool thread, with what the file holds now (a reading that did not fail).</param>
    internal TrayStatusWatcher(string runtimeHome, Action<TrayStatusReading> changed)
        : this(
            () => TrayStatusFile.Read(runtimeHome, TrayLog.Write),
            changed,
            TrayLog.Write,
            TimeProvider.System,
            RetryAfter,
            runtimeHome)
    {
    }

    // The reading is passed in so a test can make it fail; without a folder there is nothing to watch.
    internal TrayStatusWatcher(
        Func<TrayStatusReading> read,
        Action<TrayStatusReading> changed,
        Action<string> log,
        TimeProvider clock,
        TimeSpan retryAfter,
        string? watchedFolder = null)
    {
        _read = read;
        _changed = changed;
        _log = log;
        _clock = clock;
        _retryAfter = retryAfter;
        _settle = clock.CreateTimer(_ => Announce(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (watchedFolder is null)
        {
            return;
        }

        _watcher = new FileSystemWatcher(watchedFolder, TrayStatusFile.FileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = WatcherBufferBytes,
        };
        _watcher.Created += OnEvent;
        _watcher.Changed += OnEvent;
        _watcher.Renamed += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Error += (_, e) => OnWatcherError(e.GetException());
    }

    internal int BufferBytes => _watcher?.InternalBufferSize ?? 0;

    /// <summary>Starts listening, then reports what the file holds now, so a write made before this is not missed.</summary>
    internal void Start()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = true;
        }
        Announce();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _settle.Dispose();
    }

    /// <summary>
    /// The watcher lost events (its buffer overflowed) or failed. The file may have changed unseen, so it is read again; the
    /// failure is logged at most once a minute, so a watcher that keeps failing cannot fill the log.
    /// </summary>
    internal void OnWatcherError(Exception error)
    {
        var now = _clock.GetTimestamp();
        var due = false;
        lock (_settle)
        {
            if (_lastErrorLogged is not { } last || _clock.GetElapsedTime(last, now) >= ErrorLogInterval)
            {
                _lastErrorLogged = now;
                due = true;
            }
        }
        if (due)
        {
            _log($"Watching {TrayStatusFile.FileName} failed ({error.Message}); the tray reads it again now.");
        }
        OnEvent(this, EventArgs.Empty);
    }

    private void OnEvent(object? sender, EventArgs e) => _settle.Change(Settle, Timeout.InfiniteTimeSpan);

    private void Announce()
    {
        var reading = _read();
        if (!reading.Failed)
        {
            _retried = false;
            _changed(reading);
            return;
        }
        if (!_retried)
        {
            _retried = true;
            _settle.Change(_retryAfter, Timeout.InfiniteTimeSpan);
            return;
        }
        _retried = false;
        _log($"{TrayStatusFile.FileName} still could not be read; the tray keeps the status it has.");
    }
}
