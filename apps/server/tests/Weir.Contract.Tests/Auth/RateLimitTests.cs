using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Auth;

/// <summary>
/// Sign-in and bootstrap rate limiting, as a client sees it. Login and bootstrap each have a per-address sliding window
/// configured by <c>WEIR_AUTH_LOGIN_RATE_*</c> and <c>WEIR_BOOTSTRAP_RATE_*</c>; exceeding it answers 429 with <c>Retry-After</c>.
/// </summary>
[ContractArea("auth")]
public sealed class RateLimitTests(RateLimitTests.RateLimitServerFixture fixture) : AuthTestBase(fixture), IClassFixture<RateLimitTests.RateLimitServerFixture>
{
    // The limiter stamps an attempt when the request arrives, and a wrong-password attempt then spends the rest of its
    // time in Argon2. On a CI runner busy with other test classes that was 10 to 23 seconds an attempt, so the first stamp
    // aged out of the window before the third attempt arrived (run 42320939793). The attempts here are rejected for their
    // CSRF token, which the server checks after counting the attempt and before any hashing: they count the same and cost
    // milliseconds, so the three of them sit far inside the window however loaded the machine is.
    private const int WindowSeconds = 30;

    [Fact]
    public async Task Login_rate_limit_window_slides()
    {
        var session = NewSession();
        Assert.Equal(HttpStatusCode.BadRequest, (await CountedLoginAttemptAsync(session)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CountedLoginAttemptAsync(session)).Status);
        var limited = await CountedLoginAttemptAsync(session);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal(WindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), limited.Header("Retry-After"));

        await Poll.UntilAsync(
            async () => (await CountedLoginAttemptAsync(session)).Status == HttpStatusCode.BadRequest,
            "the login window to slide past the earlier attempts",
            TimeSpan.FromSeconds(WindowSeconds * 5));
    }

    private static Task<WeirResponse> CountedLoginAttemptAsync(WeirClient session) =>
        session.PostAsync(
            $"{WeirClient.Api}/auth/login",
            new JsonObject
            {
                ["username"] = WeirClient.AdminUsername,
                ["password"] = "wrong",
                ["csrf_token"] = "invalid-token",
            });

    [Fact]
    public async Task Bootstrap_rate_limited()
    {
        var session = NewSession();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await session.BootstrapAsync("owner1", "password1234")).Status);
        }

        var limited = await session.BootstrapAsync("owner1", "password1234");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("3600", limited.Header("Retry-After"));
    }

    public sealed class RateLimitServerFixture : ServerFixture
    {
        protected override IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
        {
            ["WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS"] = "2",
            ["WEIR_AUTH_LOGIN_RATE_WINDOW_SECONDS"] = WindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["WEIR_BOOTSTRAP_RATE_MAX_ATTEMPTS"] = "2",
            ["WEIR_BOOTSTRAP_RATE_WINDOW_SECONDS"] = "3600",
        };
    }
}
