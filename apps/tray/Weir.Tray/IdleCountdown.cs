namespace Weir.Tray;

/// <summary>
/// Counts how long the server has been idle without a break. Any answer other than idle, busy or none the tray can
/// trust, starts the count again: an update installs only after the whole period of quiet, never after a total of
/// quiet spread over busy stretches.
/// </summary>
sealed class IdleCountdown(TimeSpan period)
{
    private DateTimeOffset? _idleSince;

    /// <summary>Takes the server's latest answer, given at <paramref name="now"/>. True once it has been idle for the whole period.</summary>
    internal bool Observe(ServerWork work, DateTimeOffset now)
    {
        if (work == ServerWork.NotRead)
        {
            // The answer was being replaced. It is neither quiet nor busy, so the count stays where it is and the next look decides.
            return false;
        }
        if (work != ServerWork.Idle)
        {
            _idleSince = null;
            return false;
        }
        _idleSince ??= now;
        return now - _idleSince.Value >= period;
    }
}
