using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The handlers that reach media managers, download clients and the poster service connect to the address they resolved and checked
/// themselves, so a system proxy has no part in them. Asking for one is not free: on Windows .NET looks the proxy up with a call that
/// waits for the network (the search for a proxy configuration script), on the thread of the request that asked, and the first
/// requests after a machine starts, several at once, hold every thread of a pool that begins with as many as there are processors.
/// </summary>
public sealed class ManagerHandlerProxyTests
{
    [Theory]
    [InlineData(false, ManagerAddressPolicy.Local)]
    [InlineData(true, ManagerAddressPolicy.Local)]
    [InlineData(false, ManagerAddressPolicy.Public)]
    [InlineData(true, ManagerAddressPolicy.Public)]
    public void A_handler_never_looks_for_a_system_proxy(bool followRedirects, ManagerAddressPolicy policy)
    {
        using var factory = new SocketsManagerHttpHandlerFactory();

        var handler = Assert.IsType<SocketsHttpHandler>(factory.Handler(followRedirects, policy));

        Assert.False(handler.UseProxy);
    }
}
