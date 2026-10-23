using System.Net;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>How the manager client words a path, a key and a busy answer, and which failures it calls the network's.</summary>
public sealed class MediaManagerHttpClientEdgeTests
{
    private const string Base = "http://manager.local";

    [Theory]
    [InlineData("api/v3/queue", "http://manager.local/api/v3/queue")]
    [InlineData("/api/v3/queue", "http://manager.local/api/v3/queue")]
    public void A_path_gets_its_leading_slash_whether_or_not_it_was_given_one(string path, string expected) =>
        Assert.Equal(expected, new MediaManagerHttpClient(Base, "key", new FakeManagerHttp()).Url(path));

    [Fact]
    public async Task A_client_with_no_key_sends_an_empty_one()
    {
        var manager = new FakeManagerHttp().Json(HttpMethod.Get, "/health", "{}");
        var client = new MediaManagerHttpClient(Base, null!, manager);

        await client.HealthOkAsync("/health");

        Assert.Equal(string.Empty, manager.Requests.Single().Headers["X-Api-Key"]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("30", 30.0)]
    public async Task A_busy_answer_carries_the_wait_the_manager_asked_for_when_it_asked(string? retryAfter, double? expected)
    {
        var manager = new FakeManagerHttp().Route(
            HttpMethod.Get,
            "/health",
            _ => retryAfter is null ? FakeManagerHttp.Response((HttpStatusCode)429) : FakeManagerHttp.Response((HttpStatusCode)429, null, ("Retry-After", retryAfter)));
        var client = new MediaManagerHttpClient(Base, "key", manager);

        var busy = await Assert.ThrowsAsync<MediaManagerRateLimitedException>(() => client.HealthOkAsync("/health"));

        Assert.Equal(expected, busy.RetryAfterSeconds);
    }

    [Fact]
    public void No_Retry_After_header_means_no_wait() =>
        Assert.Null(MediaManagerHttpClient.RetryAfterSeconds(null));

    [Fact]
    public void A_cancelled_request_is_not_a_failure_of_the_network()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.True(MediaManagerHttpClient.IsTransportFailure(new HttpRequestException("reset"), CancellationToken.None));
        Assert.True(MediaManagerHttpClient.IsTransportFailure(new IOException("reset"), cancelled.Token));
        Assert.True(MediaManagerHttpClient.IsTransportFailure(new TaskCanceledException("timed out"), CancellationToken.None));
        Assert.False(MediaManagerHttpClient.IsTransportFailure(new TaskCanceledException("cancelled"), cancelled.Token));
        Assert.False(MediaManagerHttpClient.IsTransportFailure(new InvalidOperationException("bug"), CancellationToken.None));
    }

    [Fact]
    public async Task A_manager_that_resolves_to_this_machine_is_refused_when_only_public_addresses_are_allowed()
    {
        using var factory = new SocketsManagerHttpHandlerFactory((_, _) => Task.FromResult(new[] { IPAddress.Loopback }), time: TimeProvider.System);
        using var client = new HttpClient(factory.Handler(followRedirects: false, ManagerAddressPolicy.Public), disposeHandler: false);

        var refused = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://elsewhere.example:1234/health"));

        Assert.IsType<ManagerAddressRefusedException>(refused.InnerException);
    }
}
