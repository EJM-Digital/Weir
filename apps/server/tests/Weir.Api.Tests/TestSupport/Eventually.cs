namespace Weir.Api.Tests;

/// <summary>
/// Polls a condition until it is true or a ceiling elapses, replacing the ad hoc polling loops that used to
/// be copied into each startup/endpoint test.
/// </summary>
internal static class Eventually
{
    private static readonly TimeSpan DefaultCeiling = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ProvokeInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    public static Task ThatAsync(Func<bool> condition, TimeSpan? ceiling = null, string? message = null) =>
        ThatAsync(() => Task.FromResult(condition()), ceiling, message);

    public static async Task ThatAsync(Func<Task<bool>> condition, TimeSpan? ceiling = null, string? message = null)
    {
        var deadline = DateTime.UtcNow + (ceiling ?? DefaultCeiling);
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(message ?? "Condition was not met in time.");
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for <paramref name="pending"/>, running <paramref name="provoke"/> again every few hundred milliseconds until it completes:
    /// for a stream that only reports a change made after it began watching, so a first change can land before it does. It ends the
    /// moment <paramref name="pending"/> completes; the ceiling is a hang guard.
    /// </summary>
    public static async Task<T> ProvokingAsync<T>(Task<T> pending, Func<Task> provoke, TimeSpan? ceiling = null)
    {
        var deadline = DateTime.UtcNow + (ceiling ?? DefaultCeiling);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            await provoke().ConfigureAwait(false);
            await Task.WhenAny(pending, Task.Delay(ProvokeInterval)).ConfigureAwait(false);
        }

        return await pending.WaitAsync(HangGuard).ConfigureAwait(false);
    }

    /// <summary>Polls until <paramref name="isDone"/> accepts the polled value, and returns that value either
    /// way - the caller asserts on it, so a timeout still fails with the value's own mismatch rather than a
    /// generic "took too long" message.</summary>
    public static async Task<T> PollAsync<T>(Func<Task<T>> poll, Func<T, bool> isDone, TimeSpan? ceiling = null)
    {
        var deadline = DateTime.UtcNow + (ceiling ?? DefaultCeiling);
        while (true)
        {
            var value = await poll().ConfigureAwait(false);
            if (isDone(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }
}
