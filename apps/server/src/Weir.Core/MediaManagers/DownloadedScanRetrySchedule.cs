namespace Weir.Core.MediaManagers;

/// <summary>
/// When Weir asks a Sonarr or Radarr to scan again after it said it was busy with another scan. A worker never waits for the other
/// app, so each new ask is a job held back until its time (the same <c>not_before</c> a failed job's back-off uses). The wait grows
/// with each busy answer, a <c>Retry-After</c> the app gave raises it (never past <see cref="LongestWait"/>), and after the last wait
/// Weir stops asking.
/// </summary>
public static class DownloadedScanRetrySchedule
{
    /// <summary>The wait before each new ask, in order: the first busy answer is followed by the first wait.</summary>
    public static readonly IReadOnlyList<TimeSpan> Waits =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
    ];

    /// <summary>The longest wait between two asks, however long a <c>Retry-After</c> says.</summary>
    public static TimeSpan LongestWait => Waits[^1];

    /// <summary>How many times Weir asks again after the first ask.</summary>
    public static int MostAsksAgain => Waits.Count;

    /// <summary>
    /// How long to wait before asking again after the app has said it is busy <paramref name="busyAnswers"/> times in a row, or null
    /// when that was the last answer Weir waits for. The wait is longer when the app asked for <paramref name="retryAfter"/>.
    /// </summary>
    public static TimeSpan? WaitAfter(int busyAnswers, TimeSpan? retryAfter)
    {
        if (busyAnswers < 1 || busyAnswers > Waits.Count)
        {
            return null;
        }

        var wait = Waits[busyAnswers - 1];
        if (retryAfter is { } asked && asked > wait)
        {
            wait = asked;
        }

        return wait > LongestWait ? LongestWait : wait;
    }
}
