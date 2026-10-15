using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;

namespace Weir.E2E.Tests;

/// <summary>The rig's failure evidence reads its database without keeping it, so a failed test's real failure is not replaced by a teardown that cannot delete the folder.</summary>
public sealed class ProcessingRigEvidenceTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task Reading_the_evidence_leaves_the_rigs_database_free_to_delete()
    {
        var rig = await ProcessingRig.StartAsync();

        var evidence = rig.Evidence();

        Assert.Contains("jobs:", evidence, StringComparison.Ordinal);
        await rig.DisposeAsync();
    }
}
