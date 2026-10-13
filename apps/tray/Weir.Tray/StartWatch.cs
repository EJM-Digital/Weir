namespace Weir.Tray;

/// <summary>What <see cref="StartWatch"/> made of the latest reading.</summary>
/// <param name="Hung">The server has been silent longer than it is allowed to be.</param>
/// <param name="Where">The phase it was last heard in, in words, or "before it reported anything".</param>
/// <param name="SilentFor">How long since it last reported (or since it began, if it never has).</param>
/// <param name="Allowed">How long it was allowed to be silent in that phase.</param>
readonly record struct StartVerdict(bool Hung, string Where, TimeSpan SilentFor, TimeSpan Allowed);

/// <summary>
/// Tells a server that is working from one that has stopped, from the progress it reports. A server is working while its
/// progress keeps changing; it is hung when it has not changed for as long as that phase is allowed. What counts as a change is
/// the record itself being different from the last one read, judged on the tray's own clock: the server's clock is never compared
/// with it, so a clock that jumps cannot make a start look hung or alive.
/// </summary>
sealed class StartWatch
{
    // Two readings of one machine's clock a moment apart (the server's write, the tray's reading of when the process began).
    private static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(2);

    private readonly StartPolicy _policy;
    private readonly TimeProvider _time;
    private readonly int _processId;
    private readonly DateTimeOffset _processStarted;
    private StartProgress? _last;
    private long _changedAt;

    /// <param name="policy">How long silence is allowed.</param>
    /// <param name="time">The clock silence is measured on.</param>
    /// <param name="processId">The server process this watch is for: a reading of any other process is ignored.</param>
    /// <param name="processStarted">When that process began: a reading written before it is ignored, whatever its process id.</param>
    internal StartWatch(StartPolicy policy, TimeProvider time, int processId, DateTimeOffset processStarted)
    {
        _policy = policy;
        _time = time;
        _processId = processId;
        _processStarted = processStarted;
        _changedAt = time.GetTimestamp();
    }

    /// <summary>The latest reading the watch took as this server's, or null when it has had none.</summary>
    internal StartProgress? Last => _last;

    /// <summary>Takes <paramref name="reading"/> (null when the file is missing or unreadable) and says whether the server is hung.</summary>
    internal StartVerdict Check(StartProgress? reading)
    {
        if (reading is not null && IsThisServers(reading) && reading != _last)
        {
            _last = reading;
            _changedAt = _time.GetTimestamp();
        }

        var silent = _time.GetElapsedTime(_changedAt);
        var allowed = _last is null ? _policy.FirstProgressWindow : _policy.SilenceAllowed(_last.ExpectWithin);
        var where = _last is null ? "before it reported anything" : _last.Words;
        return new StartVerdict(silent >= allowed, where, silent, allowed);
    }

    // A file of another process, or one written before this process began, is an earlier start's. The second guard covers an
    // earlier process that had this one's id.
    private bool IsThisServers(StartProgress reading) =>
        reading.ProcessId == _processId && reading.At >= _processStarted - ClockSlack;
}
