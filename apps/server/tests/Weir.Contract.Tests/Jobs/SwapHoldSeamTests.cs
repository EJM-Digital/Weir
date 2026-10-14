using System.Net;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>
/// The setting that stops a library swap for good is for tests. A real install, which runs as production, refuses to start
/// with it set rather than stop every library clean part-way through.
/// </summary>
[ContractArea("jobs")]
public sealed class SwapHoldSeamTests
{
    private const string Message = "WEIR_LIBRARY_SWAP_HOLD_AT is a setting for tests only.";

    [Theory]
    [InlineData(null)]
    [InlineData("production")]
    public async Task A_server_running_as_production_refuses_to_start_with_the_swap_hold_set(string? env)
    {
        var settings = new Dictionary<string, string> { [SwapWorld.HoldSetting] = "OriginalMovedAside" };
        if (env is not null)
        {
            settings[SwapWorld.NotProductionSetting] = env;
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => WeirServer.StartNewAsync(settings));

        Assert.Contains("exited with code 1 before it was ready", failure.Message);
        Assert.Contains(Message, failure.Message);
    }

    [Fact]
    public async Task A_server_not_running_as_production_starts_with_the_swap_hold_set()
    {
        await using var server = await WeirServer.StartNewAsync(new Dictionary<string, string>
        {
            [SwapWorld.HoldSetting] = "OriginalMovedAside",
            [SwapWorld.NotProductionSetting] = SwapWorld.NotProduction,
        });

        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).Status);
    }
}
