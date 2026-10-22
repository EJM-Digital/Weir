namespace Weir.Tray;

/// <summary>
/// How a second launch reaches the Weir that is already running. The second process has no icon of its own to speak
/// from, so it sets a named event and exits, and the running tray, which waits on the event, tells the person from its
/// own icon. The name is per Windows session, like the tray's single-instance mutex.
/// </summary>
/// <remarks>
/// The first process listens from the moment it holds the mutex, long before it has an icon (it may be asking for a port
/// or installing an update), so a second launch in that time is not lost: it is kept and delivered to whoever
/// subscribes first.
/// </remarks>
sealed class SecondLaunchSignal : IDisposable
{
    internal const string DefaultName = RuntimeHomeStart.Names.SessionPrefix + "WeirTraySecondLaunch";

    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _registration;
    private readonly Lock _gate = new();
    private Action? _launched;
    private bool _pending;

    internal SecondLaunchSignal(string name = DefaultName)
    {
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        _registration = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => OnSignalled(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Calls <paramref name="launched"/>, on a thread-pool thread, each time a second launch signals, and once now if one
    /// signalled before anyone was listening.
    /// </summary>
    internal void Subscribe(Action launched)
    {
        bool deliverNow;
        lock (_gate)
        {
            _launched = launched;
            deliverNow = _pending;
            _pending = false;
        }
        if (deliverNow)
        {
            launched();
        }
    }

    /// <summary>Whether a second launch has signalled that nobody has yet been told about.</summary>
    internal bool HasUndelivered
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Tells the running tray that Weir was started again. Does nothing when no tray is listening.</summary>
    internal static void Raise(string name = DefaultName)
    {
        if (EventWaitHandle.TryOpenExisting(name, out var existing))
        {
            using (existing)
            {
                existing.Set();
            }
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _event.Dispose();
    }

    private void OnSignalled()
    {
        Action? launched;
        lock (_gate)
        {
            launched = _launched;
            _pending = launched is null;
        }
        launched?.Invoke();
    }
}
