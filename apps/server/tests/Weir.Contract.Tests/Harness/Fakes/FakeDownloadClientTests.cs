using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>Checks the fake download client over real HTTP: each kind lets in the credentials it expects, refuses any other, and writes every attempt down.</summary>
[ContractArea("harness")]
public sealed class FakeDownloadClientTests : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false });

    public void Dispose() => _http.Dispose();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static AuthenticationHeaderValue Basic(string user, string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));

    [Fact]
    public async Task A_qbittorrent_fake_logs_in_with_the_expected_password_and_only_then_answers_with_the_session_cookie()
    {
        using var client = FakeDownloadClient.Start("qbittorrent", "alice", "right", "/downloads");
        Task<HttpResponseMessage> Login(string password) => _http.PostAsync(
            $"{client.BaseUrl}/api/v2/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "alice", ["password"] = password }));

        var refused = await Login("wrong");
        var accepted = await Login("right");
        var withoutSession = await _http.GetAsync($"{client.BaseUrl}/api/v2/app/preferences");
        using var withSession = new HttpRequestMessage(HttpMethod.Get, $"{client.BaseUrl}/api/v2/app/preferences");
        withSession.Headers.Add("Cookie", accepted.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        var preferences = JsonNode.Parse(await (await _http.SendAsync(withSession)).Content.ReadAsStringAsync())!;

        Assert.Equal("Fails.", await refused.Content.ReadAsStringAsync());
        Assert.Equal("Ok.", await accepted.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, withoutSession.StatusCode);
        Assert.Equal("/downloads", (string)preferences["save_path"]!);
        Assert.Equal([new SignInAttempt("alice", "wrong", false), new SignInAttempt("alice", "right", true)], client.Attempts);
    }

    [Fact]
    public async Task A_sabnzbd_fake_answers_its_settings_to_the_expected_api_key_and_refuses_any_other()
    {
        using var client = FakeDownloadClient.Start("sabnzbd", null, "key", "/downloads/complete");

        var refused = await _http.GetAsync($"{client.BaseUrl}/api?mode=get_config&section=misc&output=json&apikey=other");
        var accepted = JsonNode.Parse(await _http.GetStringAsync($"{client.BaseUrl}/api?mode=get_config&section=misc&output=json&apikey=key"))!;
        var missing = await _http.GetAsync($"{client.BaseUrl}/api?mode=get_config&section=misc&output=json");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal("/downloads/complete", (string)accepted["config"]!["misc"]!["complete_dir"]!);
        Assert.Equal([false, true, false], client.Attempts.Select(attempt => attempt.Accepted));
        Assert.Equal(["other", "key", string.Empty], client.Attempts.Select(attempt => attempt.Secret));
    }

    [Fact]
    public async Task An_nzbget_fake_wants_basic_authentication_and_follows_a_changed_password()
    {
        using var client = FakeDownloadClient.Start("nzbget", "nzb", "right");
        async Task<HttpResponseMessage> Call(AuthenticationHeaderValue? auth)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{client.BaseUrl}/jsonrpc") { Content = Json("""{"method":"config","params":[]}""") };
            request.Headers.Authorization = auth;
            return await _http.SendAsync(request);
        }

        var anonymous = await Call(null);
        var accepted = await Call(Basic("nzb", "right"));
        client.Require("nzb", "changed");
        var afterChange = await Call(Basic("nzb", "right"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterChange.StatusCode);
        Assert.Equal([false, true, false], client.Attempts.Select(attempt => attempt.Accepted));
    }

    [Fact]
    public async Task A_transmission_fake_checks_the_login_first_and_then_asks_for_its_session_id()
    {
        using var client = FakeDownloadClient.Start("transmission", "tm", "right");
        async Task<HttpResponseMessage> Call(string secret, string? sessionId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{client.BaseUrl}/transmission/rpc") { Content = Json("""{"method":"session-get"}""") };
            request.Headers.Authorization = Basic("tm", secret);
            if (sessionId is not null)
            {
                request.Headers.Add("X-Transmission-Session-Id", sessionId);
            }

            return await _http.SendAsync(request);
        }

        var refused = await Call("wrong", null);
        var handshake = await Call("right", null);
        var accepted = await Call("right", handshake.Headers.GetValues("X-Transmission-Session-Id").Single());

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, handshake.StatusCode);
        Assert.Equal("success", (string)JsonNode.Parse(await accepted.Content.ReadAsStringAsync())!["result"]!);
    }

    [Fact]
    public async Task A_deluge_fake_answers_true_to_the_expected_password_and_false_to_any_other()
    {
        using var client = FakeDownloadClient.Start("deluge", null, "right");
        async Task<JsonNode> Login(string password) => JsonNode.Parse(await (await _http.PostAsync(
            $"{client.BaseUrl}/json", Json($$"""{"method":"auth.login","params":["{{password}}"],"id":1}"""))).Content.ReadAsStringAsync())!;

        var refused = await Login("wrong");
        var accepted = await Login("right");

        Assert.False((bool)refused["result"]!);
        Assert.True((bool)accepted["result"]!);
        Assert.Equal([false, true], client.Attempts.Select(attempt => attempt.Accepted));
    }

    [Fact]
    public void A_kind_that_is_not_a_download_client_is_refused()
    {
        Assert.Throws<ArgumentException>(() => FakeDownloadClient.Start("sonarr", null, "secret"));
    }
}
