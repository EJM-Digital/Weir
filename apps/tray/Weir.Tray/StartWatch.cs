namespace Weir.Tray;

/// <summary>What <see cref="StartWatch"/> made of the latest readings.</summary>
/// <param name="Hung">The server has been silent longer than it is allowed to be.</param>
/// <param name="Where">The phase it was last heard in, in words, or "before it reported anything".</param>
/// <param name="SilentFor">How long since it last reported (or since it began, if it never has).</param>
/// <param name="Allowed">How long it was allowed to be silent in that phase.</param>
readonly record struct StartVerdict(bool Hung, string Where, TimeSpan SilentFor, TimeSpan Allowed);

/// <summary>
/// Tells a server that is working from one that has stopped, from the progress it reports. A server is working while its
/// progress keeps changing; it is hung when it has not changed for as long as that phase is allowed. The progress comes by two
/// channels (the server's standard output and startup-progress.json), and it has changed when the record on either is different from
/// the last one read from that channel. Nothing is ever concluded from comparing clocks: a record is progress because it is not the
/// record before it, so a clock that steps back (a virtual machine's time sync, a snapshot resumed) cannot make good records look
/// stale, and one that jumps forward cannot make a silent server look alive. A record of another process is not this server's and is
/// not looked at.
/// </summary>
sealed class StartWatch
{
    private readonly StartPolicy _policy;
    private readonly TimeProvider _time;
    private readonly int _processId;
    private StartProgress? _fromPipe;
    private StartProgress? _fromFile;
    private StartProgress? _latest;
    private long _changedAt;

    /// <param name="policy">How long silence is allowed.</param>
    /// <param name="time">The clock silence is measured on.</param>
    /// <param name="processId">The server process this watch is for: a record of any other process is ignored.</param>
    internal StartWatch(StartPolicy policy, TimeProvider time, int processId)
    {
        _policy = policy;
        _time = time;
        _processId = processId;
        _changedAt = time.GetTimestamp();
    }

    /// <summary>The record that last changed, on either channel, or null when this server has reported none.</summary>
    internal StartProgress? Last => _latest;

    /// <summary>
    /// This server process, and no other, has reported on either channel that its listener is up. Once true it stays true: a late
    /// reading of an earlier phase from the other channel does not undo it.
    /// </summary>
    internal bool ReportedReady { get; private set; }

    /// <summary>
    /// Takes what each channel holds now (null when it holds nothing, or could not be read this time, which is no news and not silence
    /// by itself) and says whether the server is hung.
    /// </summary>
    internal StartVerdict Check(StartProgress? fromPipe, StartProgress? fromFile)
    {
        // The file first, so that when both change together the pipe's record is the one remembered.
        if (Changed(fromFile, ref _fromFile) | Changed(fromPipe, ref _fromPipe))
        {
            _changedAt = _time.GetTimestamp();
        }

        var silent = _time.GetElapsedTime(_changedAt);
        var allowed = _latest is null ? _policy.FirstProgressWindow : _policy.SilenceAllowed(_latest.ExpectWithin);
        var where = _latest is null ? "before it reported anything" : _latest.Words;
        return new StartVerdict(silent >= allowed, where, silent, allowed);
    }

    private bool Changed(StartProgress? reading, ref StartProgress? seen)
    {
        if (reading is null || reading.ProcessId != _processId || reading == seen)
        {
            return false;
        }

        seen = reading;
        _latest = reading;
        ReportedReady |= reading.IsReady;
        return true;
    }
}
