using Xunit.Sdk;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// Waiting for something the server does on its own schedule. The wait ends the moment the condition holds; the timeout is a hang
/// guard, which only a test whose server has stopped making progress reaches, and no result ever depends on how long it took.
/// </summary>
public static class Poll
{
    /// <summary>The hang guard: how long a wait for something the test has already arranged may go on before the test calls it hung.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    /// <summary>Calls <paramref name="probe"/> until it returns a value, and returns that value; fails with a sentence on timeout.</summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T?>> probe, string what, TimeSpan? timeout = null)
        where T : class
    {
        var limit = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + limit;
        string last = "nothing";
        while (true)
        {
            try
            {
                var value = await probe();
                if (value is not null)
                {
                    return value;
                }

                last = "no result yet";
            }
            catch (XunitException failure)
            {
                last = failure.Message;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new XunitException($"Timed out after {limit.TotalSeconds:0}s waiting for {what}; last saw: {last}");
            }

            await Task.Delay(Interval);
        }
    }

    /// <summary>Calls <paramref name="condition"/> until it holds.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null) =>
        await UntilAsync(async () => await condition() ? "done" : null, what, timeout);
}
