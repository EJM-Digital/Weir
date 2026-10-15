namespace Weir.Core.MediaManagers;

/// <summary>
/// When Weir sends a hand-off report again after a manager answered that it could not take it yet (a 5xx, 408 or 429). The media
/// manager heartbeat runs every minute and is the earliest a report is sent again: the wait starts at one heartbeat and doubles
/// with each refusal up to an hour, a <c>Retry-After</c> the manager gave raises it (never past the hour), and a report refused
/// for a week is given up so a manager that never recovers does not keep it for ever. The same shape Deluno uses for the
/// outcomes it owes Weir.
/// </summary>
public static class ReportRetrySchedule
{
    /// <summary>The wait after the first refusal: one heartbeat.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(1);

    /// <summary>The longest wait between two attempts, however many refusals or whatever <c>Retry-After</c> says.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    /// <summary>How long after its first refusal a report is still sent again.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);

    /// <summary>The doubling stops mattering long before this: 2^12 minutes is far past <see cref="LongestWait"/>.</summary>
    private const int MostDoublings = 12;

    /// <summary>The wait after the <paramref name="refusals"/>th refusal in a row, longer when the manager asked for <paramref name="retryAfter"/>.</summary>
    public static TimeSpan Wait(int refusals, TimeSpan? retryAfter)
    {
        var doublings = Math.Clamp(refusals - 1, 0, MostDoublings);
        var wait = TimeSpan.FromTicks(FirstWait.Ticks << doublings);
        if (retryAfter is { } asked && asked > wait)
        {
            wait = asked;
        }

        return wait > LongestWait ? LongestWait : wait;
    }

    /// <summary>Whether a report first refused at <paramref name="firstRefusedAt"/> has been refused for too long to try again at <paramref name="now"/>.</summary>
    public static bool GivesUp(DateTimeOffset firstRefusedAt, DateTimeOffset now) => now - firstRefusedAt >= GiveUpAfter;
}
