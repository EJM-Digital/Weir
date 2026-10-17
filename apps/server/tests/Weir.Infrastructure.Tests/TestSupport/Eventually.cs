using Microsoft.Extensions.Time.Testing;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// Polls a condition until it is true or a ceiling elapses. Used to observe the effect of a signal or a
/// <c>FakeTimeProvider.Advance</c> call after the fact (the condition itself never depends on wall-clock
/// time), never as a substitute for a real completion signal where one is available. The ceiling is a hang guard: a test that
/// reaches it has stopped making progress, and no result ever depends on how long it took to get there.
/// </summary>
internal static class Eventually
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    public static Task ThatAsync(Func<bool> condition, TimeSpan? ceiling = null) =>
        ThatAsync(() => Task.FromResult(condition()), ceiling);

    /// <summary>
    /// Moves <paramref name="clock"/> on by <paramref name="step"/> before each look, so a timer the code under test sets after
    /// this call starts is fired as soon as it exists; the condition is what ends the wait, never the time that has passed.
    /// </summary>
    public static Task ThatAdvancingAsync(FakeTimeProvider clock, TimeSpan step, Func<bool> condition, TimeSpan? ceiling = null) =>
        ThatAsync(
            () =>
            {
                clock.Advance(step);
                return condition();
            },
            ceiling);

    public static async Task ThatAsync(Func<Task<bool>> condition, TimeSpan? ceiling = null)
    {
        var deadline = DateTime.UtcNow + (ceiling ?? HangGuard);
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Condition was not met in time.");
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }
}
