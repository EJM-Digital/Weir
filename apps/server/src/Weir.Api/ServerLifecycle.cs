namespace Weir.Api;

/// <summary>
/// Startup facts that readiness reports: when startup began and whether it finished.
/// </summary>
public sealed class ServerLifecycle
{
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private readonly DateTimeOffset _startedAtUtc;
    private volatile bool _startupComplete;

    public ServerLifecycle(TimeProvider time)
    {
        _time = time;
        _startedAt = time.GetTimestamp();
        _startedAtUtc = time.GetUtcNow();
    }

    public TimeSpan Elapsed => _time.GetElapsedTime(_startedAt);

    /// <summary>When the server started, in UTC.</summary>
    public DateTimeOffset StartedAt => _startedAtUtc;

    /// <summary>Startup finished and shutdown has not begun.</summary>
    public bool StartupComplete => _startupComplete;

    public void MarkStartupComplete() => _startupComplete = true;

    public void MarkStopping() => _startupComplete = false;
}
