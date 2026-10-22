using System.Globalization;
using Weir.Core.Configuration;

namespace Weir.Core.Tests.Configuration;

/// <summary>Settings, addresses and paths at their edges.</summary>
public sealed class ConfigurationEdgeTests
{
    [Fact]
    public void A_machine_that_cannot_say_its_name_reads_as_this_computer()
    {
        var identity = MachineIdentity.Current(() => throw new InvalidOperationException("no name"));

        Assert.Equal("this computer", identity.Name);
    }

    [Theory]
    [InlineData("http://[::1", "::1", null)]
    [InlineData("http://[::1]x", "::1", null)]
    [InlineData("http://[::1]:8080/path", "::1", 8080)]
    [InlineData("http://:80", null, 80)]
    [InlineData("1http://host", null, null)]
    public void A_url_is_split_even_when_its_brackets_or_host_are_missing(string raw, string? host, int? port)
    {
        var parsed = ValueParsing.ParseUrl(raw);

        Assert.Equal(host, parsed.Hostname);
        Assert.Equal(port, parsed.Port);
    }

    [Theory]
    [InlineData("", ".")]
    [InlineData("///srv/media", "/srv/media")]
    public void A_path_is_tidied_without_touching_the_disk(string path, string expected)
    {
        Assert.Equal(expected, ValueParsing.NormalizeLexically(path, TestRuntime.Bare(isWindows: false)));
    }

    [Fact]
    public void A_trusted_session_that_lasts_longer_than_a_number_can_count_lasts_as_long_as_one_can()
    {
        var options = TestRuntime.Load(("WEIR_SESSION_TRUSTED_IDLE_DAYS", long.MaxValue.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(long.MaxValue, options.SessionTrustedIdleMinutes);
    }

    [Theory]
    [InlineData("ftp://feed.example/releases.atom")]
    [InlineData("not an address")]
    [InlineData("file:///releases.atom")]
    public void A_release_feed_that_is_not_an_http_address_refuses_the_start(string address)
    {
        Assert.Throws<WeirConfigurationException>(() => WeirOptionsLoader.ParseReleasesFeedUrl(address));
    }
}
