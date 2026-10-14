using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// The server is ended at each point of a library clean (FL-20, P0), and the original must be back under its name with nothing
/// left over, or the cleaned file in place with the backup gone, whichever side of the commit the swap was on.
/// </summary>
[ContractArea("jobs")]
public sealed class SwapCrashRecoveryTests
{
    public static IEnumerable<object[]> Points() => SwapCrashPlan.EveryPoint(keepOriginal: false);

    [Theory]
    [MemberData(nameof(Points))]
    public Task A_server_ended_at_this_point_of_a_swap_leaves_one_intact_file_under_the_original_name(string point, bool keepOriginal) =>
        SwapCrashRecoveryChecks.RunAsync(point, keepOriginal);
}
