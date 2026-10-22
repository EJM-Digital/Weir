using Microsoft.Extensions.Time.Testing;
using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Time;

namespace Weir.Core.Tests.Platform;

/// <summary>Session rules at their limits, the labels shown for a browser, and the guards of the sign-in rate limit.</summary>
public sealed class SessionAndRateLimitEdgeTests
{
    [Fact]
    public void A_session_id_that_is_not_a_32_digit_hex_uuid_is_shown_as_it_is()
    {
        var session = new UserSessionRecord("not-a-uuid", 1, "hash", Instant(), Instant(), false, Instant(), null, "Browser");
        var proper = session with { Id = "0123456789abcdef0123456789abcdef" };

        Assert.Equal("not-a-uuid", session.PublicId);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", proper.PublicId);
    }

    private static Timestamp Instant() => Timestamp.FromUtc(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void A_span_too_long_to_hold_is_the_longest_there_is()
    {
        Assert.Equal(TimeSpan.MaxValue, SessionRules.Minutes(long.MaxValue));
        Assert.Equal(TimeSpan.MaxValue, SessionRules.Days(long.MaxValue));
        Assert.Equal(TimeSpan.FromMinutes(5), SessionRules.Minutes(5));
        Assert.Equal(TimeSpan.FromDays(2), SessionRules.Days(2));
    }

    [Fact]
    public void Adding_or_taking_away_time_stops_at_the_ends_of_the_calendar()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(DateTime.MaxValue, SessionRules.SafeAdd(now, TimeSpan.MaxValue));
        Assert.Equal(now.AddHours(1), SessionRules.SafeAdd(now, TimeSpan.FromHours(1)));
        Assert.Equal(DateTime.MinValue, SessionRules.SafeSubtract(now, TimeSpan.MaxValue));
        Assert.Equal(now.AddHours(-1), SessionRules.SafeSubtract(now, TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14) Chrome/120.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0) AppleWebKit Safari/604.1", "Safari on iOS")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_0) AppleWebKit Safari/604.1", "Safari on iOS")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) Firefox/121.0", "Firefox on Linux")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14) Safari/605.1", "Safari on macOS")]
    [InlineData("something-else", "Browser on device")]
    [InlineData(null, "Browser on device")]
    public void A_browser_is_labelled_by_its_name_and_platform(string? userAgent, string label)
    {
        Assert.Equal(label, SessionRules.ClientLabelFromUserAgent(userAgent));
    }

    [Theory]
    [InlineData(null, CookieSecureMode.Auto, false)]
    [InlineData("  HTTPS ", CookieSecureMode.Auto, true)]
    [InlineData("http", CookieSecureMode.Auto, false)]
    public void In_auto_mode_the_cookie_is_secure_only_for_a_request_that_arrived_over_https(string? scheme, CookieSecureMode mode, bool secure)
    {
        Assert.Equal(secure, SessionRules.ResolveCookieSecure(scheme, mode));
    }

    [Theory]
    [InlineData(CookieSecureMode.Always, "always")]
    [InlineData(CookieSecureMode.Never, "never")]
    [InlineData(CookieSecureMode.Auto, "auto")]
    public void The_cookie_mode_is_written_in_lower_case(CookieSecureMode mode, string text)
    {
        Assert.Equal(text, SessionRules.SecureModeText(mode));
    }

    [Theory]
    [InlineData(CookieSecureMode.Auto, "http", "https://site.test", null, false, true)]
    [InlineData(CookieSecureMode.Auto, "http", null, " HTTPS://site.test/page", false, true)]
    [InlineData(CookieSecureMode.Auto, "http", "http://site.test", null, false, false)]
    [InlineData(CookieSecureMode.Auto, null, "https://site.test", null, false, false)]
    [InlineData(CookieSecureMode.Auto, "http", "https://site.test", null, true, false)]
    [InlineData(CookieSecureMode.Always, "http", "https://site.test", null, false, false)]
    public void An_https_page_behind_plain_http_is_noticed_only_when_nothing_translates_the_scheme(
        CookieSecureMode mode, string? scheme, string? origin, string? referer, bool trustedProxy, bool blocked)
    {
        Assert.Equal(blocked, SessionRules.CookieSecureBlockedByUntranslatedTls(mode, scheme, origin, referer, trustedProxy));
    }

    [Theory]
    [InlineData(0, 60, 1)]
    [InlineData(1, 0, 60)]
    public void A_rate_limit_must_allow_at_least_one_event_over_a_window_of_some_length(long events, double window, int keys)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new SlidingWindowLimiter(events, window, new FakeTimeProvider(), keys));
    }

    [Fact]
    public void A_rate_limit_must_keep_at_least_one_key()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowLimiter(1, 60, new FakeTimeProvider(), maxKeys: 0));
    }

    [Fact]
    public void Events_without_a_key_share_one_bucket_and_expire_with_the_window()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SlidingWindowLimiter(2, 10, clock);

        Assert.True(limiter.Allow(null));
        Assert.True(limiter.Allow(string.Empty));
        Assert.False(limiter.Allow("unknown"));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(limiter.Allow(null));
        Assert.Equal(1, limiter.KeyCount);
    }

    [Fact]
    public void The_oldest_key_is_forgotten_when_there_are_more_keys_than_the_limit()
    {
        var limiter = new SlidingWindowLimiter(1, 60, new FakeTimeProvider(), maxKeys: 2);

        limiter.Allow("a");
        limiter.Allow("b");
        limiter.Allow("c");

        Assert.False(limiter.HasKey("a"));
        Assert.True(limiter.HasKey("c"));
    }

    [Theory]
    [InlineData("10.0.0.5", " , ", "10.0.0.5")]
    [InlineData("10.0.0.5", "10.0.0.7, 10.0.0.9", "10.0.0.7")]
    [InlineData("10.0.0.5", "203.0.113.9, 10.0.0.9", "203.0.113.9")]
    [InlineData("198.51.100.4", "203.0.113.9", "198.51.100.4")]
    [InlineData(null, "", "unknown")]
    public void The_client_behind_trusted_proxies_is_the_nearest_address_that_is_not_one(string? peer, string forwarded, string expected)
    {
        Assert.Equal(expected, ClientRateLimitKey.Resolve(peer, forwarded, ["10.0.0.0/8", "not a network"]));
    }

    [Fact]
    public void A_forwarded_header_is_ignored_and_reported_when_no_proxy_is_trusted()
    {
        var warned = 0;

        var key = ClientRateLimitKey.Resolve("198.51.100.4", "203.0.113.9", [], () => warned++);

        Assert.Equal("198.51.100.4", key);
        Assert.Equal(1, warned);
    }

    [Fact]
    public void An_address_that_is_not_an_address_is_in_no_network()
    {
        Assert.False(ClientRateLimitKey.InNetworks("garbage", []));
    }
}
