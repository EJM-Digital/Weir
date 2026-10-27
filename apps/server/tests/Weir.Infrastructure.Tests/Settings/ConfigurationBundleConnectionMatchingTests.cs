using Weir.Core.Json;
using Weir.Infrastructure.Settings;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>Which saved connection a backup's row is, and which sealed secrets are its own.</summary>
public sealed class ConfigurationBundleConnectionMatchingTests
{
    private static WireObject Secrets(params WireObject[] entries) => new WireObject().Set("media_managers", new WireArray([.. entries]));

    [Fact]
    public void A_connection_takes_the_sealed_secrets_that_carry_its_own_id_and_no_one_elses()
    {
        var ours = new WireObject().Set("id", 2).Set("api_key", "ours");
        var secrets = Secrets(
            new WireObject().Set("id", 1).Set("api_key", "theirs"),
            new WireObject().Set("id", "two").Set("api_key", "unreadable"),
            new WireObject().Set("api_key", "nobody's"),
            ours);

        Assert.Same(ours, ConfigurationBundleConnections.SecretsFor(secrets, new WireObject().Set("id", 2)));
        Assert.Empty(ConfigurationBundleConnections.SecretsFor(secrets, new WireObject().Set("id", 3)).Items);
        Assert.Empty(ConfigurationBundleConnections.SecretsFor(secrets, new WireObject().Set("id", "two")).Items);
        Assert.Empty(ConfigurationBundleConnections.SecretsFor(null, new WireObject().Set("id", 2)).Items);
        Assert.Empty(ConfigurationBundleConnections.SecretsFor(new WireObject().Set("media_managers", "none"), new WireObject().Set("id", 2)).Items);
    }

    [Theory]
    [InlineData("radarr", "http://nas:7878", "Radarr", true)]
    [InlineData("radarr", "HTTP://NAS:7878", "Other name", true)]
    [InlineData("radarr", "http://nas:9999", "Radarr", false)]
    [InlineData("sonarr", "http://nas:7878", "Radarr", false)]
    [InlineData("radarr", "", "radarr on nas", false)]
    public void A_connection_is_the_same_when_its_kind_and_address_agree(string kind, string baseUrl, string name, bool same)
    {
        var known = new ConfigurationBundleConnections.KnownConnection(1, "radarr", "http://nas:7878", "Radarr");

        Assert.Equal(same, known.IsSameAs(kind, baseUrl, name));
    }

    [Theory]
    [InlineData("radarr", "Radarr", true)]
    [InlineData("radarr", "RADARR", true)]
    [InlineData("radarr", "Another", false)]
    [InlineData("sonarr", "Radarr", false)]
    public void A_connection_with_no_address_is_the_same_when_its_kind_and_name_agree(string kind, string name, bool same)
    {
        var known = new ConfigurationBundleConnections.KnownConnection(1, "radarr", string.Empty, "Radarr");

        Assert.Equal(same, known.IsSameAs(kind, string.Empty, name));
        Assert.False(known.IsSameAs(kind, "http://nas:7878", name));
    }
}
