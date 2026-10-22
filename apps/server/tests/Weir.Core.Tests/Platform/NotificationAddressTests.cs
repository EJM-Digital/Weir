using Weir.Core.Json;
using Weir.Core.Notifications;

namespace Weir.Core.Tests.Platform;

/// <summary>
/// How an alert's address is read, shown and judged: the parts of an address, what is hidden when it is shown, and which addresses
/// Weir refuses to call out to.
/// </summary>
public sealed class NotificationAddressTests
{
    [Theory]
    [InlineData("[\"job_failed\", 3, \"job_completed\"]", "job_failed,job_completed")]
    [InlineData("{\"job_failed\": true, \"job_completed\": false}", "job_failed,job_completed")]
    [InlineData("\"ab\"", "a,b")]
    [InlineData("42", "")]
    [InlineData("not json", "")]
    [InlineData(null, "")]
    public void The_events_of_a_channel_are_read_from_whatever_was_stored(string? stored, string expected)
    {
        Assert.Equal(expected, string.Join(',', NotificationRules.ParseEvents(stored!)));
    }

    [Fact]
    public void A_channel_needs_a_label_a_known_provider_and_at_least_one_known_event()
    {
        string[] events = ["job_failed"];

        Assert.Equal("Label must not be empty.", Assert.Throws<WireValueException>(() => NotificationRules.Validate(null!, "webhook", "https://hooks.example.test/a", events)).Message);
        Assert.Equal("Label must not be empty.", Assert.Throws<WireValueException>(() => NotificationRules.Validate("  ", "webhook", "https://hooks.example.test/a", events)).Message);
        Assert.StartsWith("Unsupported provider: ''", Assert.Throws<WireValueException>(() => NotificationRules.Validate("Mine", null!, "https://hooks.example.test/a", events)).Message);
        Assert.StartsWith("Unknown events: nope.", Assert.Throws<WireValueException>(() => NotificationRules.Validate("Mine", "discord", "https://hooks.example.test/a", ["nope"])).Message);
        Assert.Equal("At least one event must be selected.", Assert.Throws<WireValueException>(() => NotificationRules.Validate("Mine", "discord", "https://hooks.example.test/a", [])).Message);
        NotificationRules.Validate("Mine", "discord", "https://hooks.example.test/a", events);
    }

    [Fact]
    public void An_address_that_cannot_be_split_is_hidden_whole()
    {
        Assert.Equal((NotificationRules.TokenMask, true), NotificationRules.MaskAddress("https://[::1/hook"));
        Assert.Equal((NotificationRules.TokenMask, true), NotificationRules.MaskAddress("not an address"));
        Assert.Equal((NotificationRules.TokenMask, true), NotificationRules.MaskAddress("https:///only/a/path"));
    }

    [Fact]
    public void An_address_that_cannot_be_split_is_compared_as_typed()
    {
        Assert.Equal("https://[::1/hook", NotificationRules.NormalizeAddress("  https://[::1/hook "));
        Assert.True(NotificationRules.IsSameAddress("https://[::1/hook", "  https://[::1/hook"));
    }

    [Fact]
    public void An_address_is_compared_with_its_fragment_and_a_lower_case_host()
    {
        Assert.Equal("https://example.test/Hook?Token=A#Part", NotificationRules.NormalizeAddress(" HTTPS://EXAMPLE.test/Hook/?Token=A#Part "));
        Assert.True(NotificationRules.IsSameAddress("https://example.test/Hook#a", "HTTPS://Example.Test/Hook/#a"));
        Assert.False(NotificationRules.IsSameAddress("https://example.test/Hook#a", "https://example.test/Hook#b"));
    }

    [Fact]
    public void A_job_notification_for_a_module_with_no_name_keeps_the_name_empty()
    {
        var (jobEvent, title, _) = NotificationRules.JobNotification(string.Empty, "completed", 5, "kind", "Weir on RIG");

        Assert.Equal("_job_completed", jobEvent);
        Assert.Equal(" job completed", title);
    }

    [Fact]
    public void A_job_notification_for_another_module_names_the_module_in_title_case()
    {
        var (_, title, detail) = NotificationRules.JobNotification("LIBRARY", "failed", 9, "scan", "Weir on RIG");

        Assert.Equal("Library job failed", title);
        Assert.Equal("Job 9 (scan) exhausted all retry attempts.", detail);
    }

    [Fact]
    public void The_host_of_an_address_is_read_with_or_without_brackets()
    {
        Assert.Equal("example.test", SplitUrl.Parse("https://user:pw@Example.test:8443/x").Hostname);
        Assert.Equal("Example.test", SplitUrl.Parse("https://Example.test").HostAsWritten);
        Assert.Equal("::1", SplitUrl.Parse("https://[::1]:80/x").Hostname);
        Assert.Null(SplitUrl.Parse("https://:80/x").Hostname);
    }

    [Fact]
    public void A_host_with_an_opening_bracket_but_no_closing_one_runs_to_the_end()
    {
        var split = new SplitUrl("https", "[abc", string.Empty, string.Empty, string.Empty);

        Assert.Equal("abc", split.HostAsWritten);
        Assert.Null(split.Port);
    }

    [Fact]
    public void The_sign_in_details_of_an_address_are_read_apart()
    {
        var both = SplitUrl.Parse("https://alice:secret@example.test/");
        Assert.True(both.HasUserInfo);
        Assert.Equal("alice", both.Username);
        Assert.Equal("secret", both.Password);

        var nameOnly = SplitUrl.Parse("https://alice@example.test/");
        Assert.Equal("alice", nameOnly.Username);
        Assert.Null(nameOnly.Password);

        var none = SplitUrl.Parse("https://example.test/");
        Assert.False(none.HasUserInfo);
        Assert.Null(none.Username);
        Assert.Null(none.Password);
    }

    [Theory]
    [InlineData("https://example.test:8443/x", 8443)]
    [InlineData("https://[::1]:9000/x", 9000)]
    [InlineData("https://[::1]/x", null)]
    [InlineData("https://example.test/x", null)]
    [InlineData("https://example.test:/x", null)]
    [InlineData("https://[::1]:/x", null)]
    public void The_port_of_an_address_is_read_when_it_is_written(string address, int? port)
    {
        Assert.Equal(port, SplitUrl.Parse(address).Port);
    }

    [Theory]
    [InlineData("https://example.test:80a/x", "Port could not be cast to integer value as '80a'")]
    [InlineData("https://example.test:65536/x", "Port out of range 0-65535")]
    public void A_port_that_is_not_a_number_in_range_is_refused(string address, string message)
    {
        var split = SplitUrl.Parse(address);

        Assert.Equal(message, Assert.Throws<WireValueException>(() => split.Port).Message);
    }

    [Theory]
    [InlineData("https://[::1/x", "Invalid IPv6 URL")]
    [InlineData("https://::1]/x", "Invalid IPv6 URL")]
    [InlineData("https://[v1.fe80::a]/x", null)]
    [InlineData("https://[vzz]/x", "IPvFuture address is invalid")]
    [InlineData("https://[nonsense]/x", "'nonsense' does not appear to be an IPv4 or IPv6 address")]
    [InlineData("https://[10.0.0.1]/x", "An IPv4 address cannot be in brackets")]
    public void Brackets_in_an_address_must_hold_an_address(string address, string? refusal)
    {
        if (refusal is null)
        {
            Assert.Equal("v1.fe80::a", SplitUrl.Parse(address).Hostname);
            return;
        }

        Assert.Equal(refusal, Assert.Throws<WireValueException>(() => SplitUrl.Parse(address)).Message);
    }

    [Theory]
    [InlineData("hook.example.test/path", "Blocked provider URL scheme: <missing>")]
    [InlineData("ftp://hook.example.test/path", "Blocked provider URL scheme: ftp")]
    [InlineData("https:///path", "Blocked provider URL host: <missing>")]
    [InlineData("https://LocalHost/path", "Blocked provider URL host: localhost")]
    [InlineData("https://127.0.0.2/path", "Blocked provider URL host: 127.0.0.2")]
    [InlineData("https://169.254.169.254/path", "Blocked provider URL host: 169.254.169.254")]
    [InlineData("https://10.1.2.3/path", "Blocked provider URL host: 10.1.2.3")]
    [InlineData("https://224.0.0.5/path", "Blocked provider URL host: 224.0.0.5")]
    [InlineData("https://240.0.0.1/path", "Blocked provider URL host: 240.0.0.1")]
    [InlineData("https://[::]/path", "Blocked provider URL host: ::")]
    [InlineData("https://[fe80::1]/path", "Blocked provider URL host: fe80::1")]
    [InlineData("https://[ff02::1]/path", "Blocked provider URL host: ff02::1")]
    public void An_address_that_would_reach_the_local_network_is_refused(string address, string message)
    {
        Assert.Equal(message, Assert.Throws<WireValueException>(() => ExternalUrlPolicy.ValidateExternalProviderUrl(address)).Message);
    }

    [Theory]
    [InlineData("https://hooks.example.test/abc")]
    [InlineData("http://93.184.216.34/abc")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/abc")]
    public void A_public_address_is_accepted_as_written(string address)
    {
        Assert.Equal(address, ExternalUrlPolicy.ValidateExternalProviderUrl(address));
    }
}
