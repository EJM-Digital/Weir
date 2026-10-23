using Microsoft.Extensions.DependencyInjection;
using Weir.Core.Configuration;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>What library mode registers for a swap: nothing to hold it unless a test asks for a hold, and a hold that names no point is refused at start-up.</summary>
public sealed class LibraryModeServicesTests : IDisposable
{
    private readonly StoreFixture _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Without_a_hold_nothing_is_registered_to_stop_a_swap_part_way()
    {
        var services = new ServiceCollection();

        services.AddWeirLibraryMode(_store.Options);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ISwapProgress));
    }

    [Fact]
    public void A_hold_at_a_point_of_the_swap_is_registered()
    {
        var services = new ServiceCollection();

        services.AddWeirLibraryMode(_store.Options with { LibrarySwapHoldAt = nameof(SwapPoint.CopyChecked) });

        var hold = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISwapProgress));
        Assert.IsType<HeldSwapProgress>(hold.ImplementationInstance);
    }

    [Fact]
    public void A_hold_that_names_no_point_of_the_swap_is_refused()
    {
        var services = new ServiceCollection();

        var refused = Assert.Throws<WeirConfigurationException>(() => services.AddWeirLibraryMode(_store.Options with { LibrarySwapHoldAt = "whenever" }));

        Assert.Contains("WEIR_LIBRARY_SWAP_HOLD_AT is 'whenever'", refused.Message, StringComparison.Ordinal);
    }
}
