using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>Addresses that cannot be read whole still give the connection a name.</summary>
public sealed class ConnectionNamingEdgeTests
{
    private static string NameOf(string baseUrl) =>
        ConnectionNaming.NamesFor([new ConnectionAddress(1, "Radarr", baseUrl)])[1];

    [Theory]
    [InlineData("http://[::1", "Radarr")]
    [InlineData("http://::1]:7878", "Radarr")]
    [InlineData("http://nas:99999", "Radarr on nas")]
    [InlineData("http://nas:port", "Radarr on nas")]
    public void An_address_that_cannot_be_read_is_named_as_far_as_it_can_be(string baseUrl, string expected) =>
        Assert.Equal(expected, NameOf(baseUrl));

    [Fact]
    public void Two_connections_on_one_host_with_an_unreadable_port_are_numbered_not_merged()
    {
        var names = ConnectionNaming.NamesFor(
        [
            new ConnectionAddress(1, "Radarr", "http://nas:99999"),
            new ConnectionAddress(2, "Radarr", "http://nas:port"),
        ]);

        Assert.Equal(["Radarr on nas", "Radarr on nas (2)"], [names[1], names[2]]);
    }
}
