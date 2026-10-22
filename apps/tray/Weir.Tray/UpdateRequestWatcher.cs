namespace Weir.Tray;

/// <summary>
/// Tells the tray when System › About asks for an update step, the moment the server leaves a flag file for it
/// (<c>update-check-now</c>, <c>update-download-now</c> or <c>update-apply-now</c> in the runtime home), with no timer. A
/// burst of events is looked at once, after it has settled. When the watcher loses events (its buffer overflowed) or
/// fails, it is built again and the folder is looked at at once, since a flag may have arrived unseen; if it cannot be
/// built again it is tried every few seconds until it can. The failure is logged at most once a minute, so a watcher that
/// keeps failing cannot fill the log.
/// </summary>
sealed class UpdateRequestWatcher : IDisposable
{
    /// <summary>How long a burst of events is given to finish before the folder is looked at.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    /// <summary>How long after a watcher could not be built again that building it is tried once more.</summary>
    internal static readonly TimeSpan RebuildRetry = TimeSpan.FromSeconds(5);

    /// <summary>The least time between two log lines about the watcher itself failing.</summary>
    internal static readonly TimeSpan ErrorLogInterval = TimeSpan.FromMinutes(1);

    // The three flag files and nothing else in the runtime home.
    private const string FlagFilter = "update-*-now";

    // Large enough that a burst of writes in the runtime home does not overflow the watcher's buffer.
    private const int WatcherBufferBytes = 64 * 1024;

    private readonly string _runtimeHome;
    private readonly Action _look;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;
    private readonly Func<FileSystemWatcher> _create;
    private readonly ITimer _settle;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private bool _disposed;
    private long? _lastErrorLogged;

    /// <param name="runtimeHome">The folder the flag files are left in.</param>
    /// <param name="look">Called, on a thread-pool thread, to look for flag files: once at start and after each burst of events.</param>
    /// <param name="log">Where a failing watcher says so.</param>
    /// <param name="clock">Times the least gap between two of those lines.</param>
    /// <param name="create">Builds the file watcher; a test makes it fail.</param>
    /// <exception cref="ArgumentException">The folder does not exist.</exception>
    internal UpdateRequestWatcher(string runtimeHome, Action look, Action<string> log, TimeProvider clock, Func<FileSystemWatcher>? create = null)
    {
        _runtimeHome = runtimeHome;
        _look = look;
        _log = log;
        _clock = clock;
        _create = create ?? Build;
        _settle = clock.CreateTimer(_ => Settled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = Make();
    }

    internal int BufferBytes => _watcher?.InternalBufferSize ?? 0;

    /// <summary>How many times the watcher has been built again after an error.</summary>
    internal int Rebuilt { get; private set; }

    /// <summary>Starts listening, then looks once, so a flag left before this is not missed.</summary>
    internal void Start()
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = true;
            }
        }
        _look();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _settle.Dispose();
        }
    }

    internal void OnWatcherError(Exception error)
    {
        lock (_gate)
        {
            LogOnceAMinute($"Watching for update requests failed ({error.Message}); the tray builds the watcher again and looks for them now.");
            Rebuild();
            Arm(Settle);
        }
    }

    // Called with the gate held.
    private void LogOnceAMinute(string line)
    {
        var now = _clock.GetTimestamp();
        if (_lastErrorLogged is { } last && _clock.GetElapsedTime(last, now) < ErrorLogInterval)
        {
            return;
        }
        _lastErrorLogged = now;
        _log(line);
    }

    private FileSystemWatcher Build() =>
        new(_runtimeHome, FlagFilter)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            InternalBufferSize = WatcherBufferBytes,
        };

    private FileSystemWatcher Make()
    {
        var watcher = _create();
        watcher.Created += OnEvent;
        watcher.Changed += OnEvent;
        watcher.Renamed += OnEvent;
        watcher.Error += (_, e) => OnWatcherError(e.GetException());
        return watcher;
    }

    // Called with the gate held.
    private void Rebuild()
    {
        if (_disposed)
        {
            return;
        }
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = Make();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            Rebuilt++;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            LogOnceAMinute($"The tray could not build the update request watcher again ({ex.Message}); it tries again in {RebuildRetry.TotalSeconds:0} seconds.");
        }
    }

    private void OnEvent(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            Arm(Settle);
        }
    }

    // Called with the gate held. A watcher that was disposed has no timer to arm.
    private void Arm(TimeSpan after)
    {
        if (!_disposed)
        {
            _settle.Change(after, Timeout.InfiniteTimeSpan);
        }
    }

    // The timer callback; internal so a test can run one that was already on its way when the watcher was disposed.
    internal void Settled()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (_watcher is null)
            {
                Rebuild();
                if (_watcher is null)
                {
                    Arm(RebuildRetry);
                    return;
                }
            }
        }
        _look();
    }
}
