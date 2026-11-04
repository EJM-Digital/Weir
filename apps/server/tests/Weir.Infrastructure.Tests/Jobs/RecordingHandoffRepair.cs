using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>A hand-off repair that only counts how often startup ran it.</summary>
internal sealed class RecordingHandoffRepair : IStuckHandoffRepair
{
    public int Runs { get; private set; }

    public Task<int> RepairAsync(CancellationToken cancellationToken)
    {
        Runs++;
        return Task.FromResult(0);
    }
}
