using Microsoft.Extensions.Time.Testing;

namespace Weir.Api.Tests;

/// <summary>
/// Polls a condition until it is true or a ceiling elapses. The ceiling is a hang guard: a test that reaches it has stopped making
/// progress, and no result ever depends on how long it took to get there.
/// </summary>
internal static class Eventually
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static Task ThatAsync(Func<bool> condition, TimeSpan? ceiling = null, string? message = null) =>
        ThatAsync(() => Task.FromResult(condition()), ceiling, message);

    public static async Task ThatAsync(Func<Task<bool>> condition, TimeSpan? ceiling = null, string? message = null)
    {
        var deadline = DateTime.UtcNow + (ceiling ?? HangGuard);
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
    /// Moves <paramref name="clock"/> on by <paramref name="step"/> before each look, so a timer the code under test sets after this
    /// call starts is fired as soon as it exists; the condition is what ends the wait, never the time that has passed.
    /// </summary>
    public static Task ThatAdvancingAsync(FakeTimeProvider clock, TimeSpan step, Func<bool> condition, TimeSpan? ceiling = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return ThatAsync(
            () =>
            {
                clock.Advance(step);
                return condition();
            },
            ceiling);
    }

    /// <summary>
    /// Calls <paramref name="provoke"/> at each look until <paramref name="pending"/> completes, then returns its result. For a stream
    /// that may not be watching yet when the first change is made: the change is made again until the frame it causes arrives. The
    /// pending read is never abandoned, as cancelling a read on the test host's response stream tears the connection down.
    /// </summary>
    public static async Task<T> ProvokingUntilAsync<T>(Task<T> pending, Action provoke, TimeSpan? ceiling = null)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(provoke);
        await ThatAsync(
            () =>
            {
                if (!pending.IsCompleted)
                {
                    provoke();
                }

                return pending.IsCompleted;
            },
            ceiling).ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }

    /// <summary>Polls until <paramref name="isDone"/> accepts the polled value, and returns that value either
    /// way - the caller asserts on it, so a timeout still fails with the value's own mismatch rather than a
    /// generic "took too long" message.</summary>
    public static async Task<T> PollAsync<T>(Func<Task<T>> poll, Func<T, bool> isDone, TimeSpan? ceiling = null)
    {
        var deadline = DateTime.UtcNow + (ceiling ?? HangGuard);
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
