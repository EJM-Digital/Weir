using System.Diagnostics;

namespace Weir.Infrastructure.Sqlite;

/// <summary>How a bulk job shares SQLite's single write lock with every other lane (#708).</summary>
/// <remarks>
/// <para>A bulk job (a watched-folder scan, the vanished-file sweep, retention, a library index update) writes in short
/// transactions so that no other writer waits long for any one of them. That alone is not enough. SQLite gives the lock to
/// whoever asks once it is free, and a connection that is already waiting sleeps between its tries, for up to 100 ms. A job
/// that begins its next transaction the moment its last one commits wins every time, and a hand-off or a progress update
/// ends up waiting for the whole job after all.</para>
/// <para>So after each transaction the job stands back for <see cref="PauseFactor"/> times as long as it held the lock, and
/// never less than <see cref="MinimumPause"/>. The lock is then free about three quarters of the time, so a waiting writer's
/// next try usually finds it free, and a long wait needs several unlucky tries in a row. The job takes longer, which a
/// background job can afford. No queue or shared lock is involved; the job only leaves gaps.</para>
/// </remarks>
public static class WriteLockTurns
{
    /// <summary>How many times as long as it held the lock a bulk job leaves it free before its next transaction.</summary>
    public const int PauseFactor = 3;

    /// <summary>The shortest pause between a bulk job's transactions: several of a waiting writer's early retries fit into it.</summary>
    public static readonly TimeSpan MinimumPause = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The longest pause. A transaction's time includes any wait for the lock, so a busy database lengthens the pause, and this
    /// keeps a job from idling for long after one slow turn.
    /// </summary>
    public static readonly TimeSpan MaximumPause = TimeSpan.FromSeconds(1);

    /// <summary>How long a bulk job stands back after a transaction that held the lock for <paramref name="held"/>.</summary>
    public static TimeSpan PauseAfter(TimeSpan held)
    {
        var pause = held * PauseFactor;
        return pause < MinimumPause ? MinimumPause : pause > MaximumPause ? MaximumPause : pause;
    }

    /// <summary>Runs one of a bulk job's transactions, then leaves the lock free for a while before the job's next one.</summary>
    public static async Task TakeAsync(Func<Task> transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var started = Stopwatch.GetTimestamp();
        await transaction().ConfigureAwait(false);
        await Task.Delay(PauseAfter(Stopwatch.GetElapsedTime(started)), cancellationToken).ConfigureAwait(false);
    }
}
