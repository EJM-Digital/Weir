using System.Globalization;
using System.Net;
using System.Text;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>Checks the fake alert receiver over real HTTP, so the tests that point a server at it can rely on it.</summary>
[ContractArea("harness")]
public sealed class FakeAlertReceiverTests : IDisposable
{
    private readonly FakeAlertReceiver _receiver = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public void Dispose()
    {
        _http.Dispose();
        _receiver.Dispose();
    }

    private Task<HttpResponseMessage> PostAsync(string path) =>
        _http.PostAsync(_receiver.BaseUrl + path, new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));

    [Fact]
    public async Task A_post_is_taken_and_recorded_with_its_headers_and_body()
    {
        using var answer = await PostAsync("/alerts/1");

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        var post = Assert.Single(await _receiver.WaitForPostsAsync("/alerts/1", 1));
        Assert.Equal("POST", post.Method);
        Assert.StartsWith("application/json", post.Header("Content-Type"), StringComparison.Ordinal);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(post.Body));
    }

    [Fact]
    public async Task Each_path_is_answered_as_the_test_set_it()
    {
        _receiver.Answers("/broken", HttpStatusCode.InternalServerError);
        _receiver.RedirectsTo("/moved", "http://hooks.example.com/elsewhere");

        using var refused = await PostAsync("/broken");
        using var moved = await PostAsync("/moved");
        using var fine = await PostAsync("/fine");

        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Found, moved.StatusCode);
        Assert.Equal("http://hooks.example.com/elsewhere", moved.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.NoContent, fine.StatusCode);
        Assert.Single(_receiver.PostsTo("/broken"));
        Assert.Empty(_receiver.PostsTo("/never"));
    }

    [Fact]
    public async Task A_post_for_the_public_name_a_channel_names_is_taken_as_well()
    {
        // The server connects to this port but sends the name in the address as the host, as it does for a real destination.
        using var request = new HttpRequestMessage(HttpMethod.Post, _receiver.BaseUrl + "/alerts/named")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Host = "hooks.example.com";

        using var answer = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal("hooks.example.com", Assert.Single(await _receiver.WaitForPostsAsync("/alerts/named", 1)).Header("Host"));
    }

    [Fact]
    public void The_environment_runs_a_development_server_that_sends_alerts_to_this_port()
    {
        var env = _receiver.Env;

        Assert.Equal("development", env["WEIR_ENV"]);
        Assert.Equal(new Uri(_receiver.BaseUrl).Port.ToString(CultureInfo.InvariantCulture), env["WEIR_NOTIFICATION_STAND_IN_PORT"]);
    }
}
