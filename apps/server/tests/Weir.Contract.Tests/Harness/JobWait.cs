using Xunit.Sdk;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// Waiting for what a job produces, for as long as Weir is working on the job. How long a scan or a clean takes depends on the machine,
/// and the media tools it runs bound themselves (they are stopped when they go silent), so a job that a worker holds is waited
/// for. What a test cannot wait on is a job nobody takes up, which fails after <see cref="UnclaimedCeiling"/>, and a job that ended
/// without producing its result, which fails at once.
/// </summary>
internal sealed class JobWait(TimeProvider clock, Action? polled = null)
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
