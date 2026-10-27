using Xunit.Sdk;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// Waiting for what a job produces, for as long as Weir is working on the job. How long a scan or a clean takes depends on the machine:
/// the media tools run at below-normal priority so that everything else comes first, and on a saturated machine they are given the
/// processor in short turns, so a clean of a few seconds of film can take minutes. The tools bound themselves (they are stopped when
/// they go silent), so a job that a worker holds is waited for. What a test cannot wait on is a job nobody takes up, which fails after <see cref="UnclaimedCeiling"/>, and a job that ended
/// without producing its result, which fails at once.
/// </summary>
public sealed class JobWait(TimeProvider clock, Action? polled = null)
{
    /// <summary>How long a job may wait for a worker, which picks up work within seconds, before the wait gives up.</summary>
    public static readonly TimeSpan UnclaimedCeiling = TimeSpan.FromSeconds(120);

    private const string Pending = "pending";
    private const string Leased = "leased";

    public JobWait()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Polls until no job is in a status <paramref name="busy"/> names. Each poll judges one reading of the jobs: Weir queues work on its
    /// own timer (the first scan of a new workflow), so a job can appear between two reads, and that is more work to wait for, not jobs
    /// that ended without a result. Jobs that wait for a worker are held to <see cref="UnclaimedCeiling"/> as in <see cref="UntilAsync{T}"/>.
    /// </summary>
    public async Task UntilNoneAsync(Func<Task<IReadOnlyList<string>>> jobStatuses, Func<string, bool> busy, string what)
    {
        IReadOnlyList<string> reading = [];
        await UntilAsync(
            async () => reading = await jobStatuses(),
            () => Task.FromResult(reading.Any(busy) ? null : "quiet"),
            what).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls until <paramref name="result"/> returns a value. <paramref name="jobStatuses"/> says what state the jobs that produce
    /// it are in (<c>pending</c>, <c>leased</c> while a worker has one, or an ended status); <paramref name="what"/> finishes the
    /// sentence "waiting for ...".
    /// </summary>
    public async Task<T> UntilAsync<T>(Func<Task<IReadOnlyList<string>>> jobStatuses, Func<Task<T?>> result, string what)
        where T : class
    {
        DateTimeOffset? notWorkedOnSince = null;
        var seen = "nothing yet";
        while (true)
        {
            IReadOnlyList<string>? statuses = null;
            try
            {
                // The jobs are read before the result: a job seen ended recorded its result before it ended, so the read after it shows it.
                statuses = await jobStatuses();
                if (await result() is { } found)
                {
                    return found;
                }

                seen = $"jobs {(statuses.Count == 0 ? "none" : string.Join(", ", statuses))}, no result yet";
            }
            catch (XunitException failure)
            {
                seen = failure.Message;
                statuses = null;
            }

            polled?.Invoke();
            if (statuses?.Contains(Leased) == true)
            {
                notWorkedOnSince = null;
            }
            else
            {
                if (statuses is not null && !statuses.Contains(Pending))
                {
                    throw new XunitException($"Weir's jobs ended ({(statuses.Count == 0 ? "none left" : string.Join(", ", statuses))}) but {what} never appeared.");
                }

                var now = clock.GetUtcNow();
                notWorkedOnSince ??= now;
                if (now - notWorkedOnSince >= UnclaimedCeiling)
                {
                    throw new XunitException(
                        $"Nothing was working on the job for {UnclaimedCeiling.TotalSeconds:0}s while waiting for {what}; last saw: {seen}");
                }
            }

            await Task.Delay(Poll.Interval);
        }
    }
}
