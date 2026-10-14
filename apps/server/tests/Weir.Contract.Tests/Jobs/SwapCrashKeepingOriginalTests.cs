using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// The same crashes with "keep the original after clean" on: the place the original goes is reserved before the commit, so a
/// crash leaves an empty reservation to release, or the original in it, and neither may be lost.
/// </summary>
[ContractArea("jobs")]
public sealed class SwapCrashKeepingOriginalTests
{
    public static IEnumerable<object[]> Points() => SwapCrashPlan.EveryPoint(keepOriginal: true);

    [Theory]
    [MemberData(nameof(Points))]
    public Task A_server_ended_at_this_point_of_a_swap_that_keeps_the_original_loses_neither_file(string point, bool keepOriginal) =>
        SwapCrashRecoveryChecks.RunAsync(point, keepOriginal);
}
