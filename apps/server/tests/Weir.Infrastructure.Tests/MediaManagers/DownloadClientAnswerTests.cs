using System.Net;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>What each download-client dialect makes of an answer that is not the one its client gives: another status, a page where JSON should be, JSON of another shape.</summary>
public sealed class DownloadClientAnswerTests
{
    private const string NotTheClient = "did not get the answer it expected";

    private static DownloadClientConnection Connection(string kind, string? apiKey = null, string? username = null, string? password = null) =>
        new(kind, "Main", "http://client.local", username, password, apiKey, 1);

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    public void Only_a_2xx_status_is_a_success(int status, bool succeeded) =>
        Assert.Equal(succeeded, new DownloadClientHttpResponse(status, [], new Dictionary<string, string>()).Succeeded);

    [Theory]
    [InlineData(200, "<html>not sabnzbd</html>", NotTheClient)]
    [InlineData(200, "[]", NotTheClient)]
    [InlineData(200, "{}", NotTheClient)]
    [InlineData(200, """{"config":3}""", NotTheClient)]
    [InlineData(500, "", NotTheClient)]
    [InlineData(401, "", "API key was refused")]
    [InlineData(403, "", "API key was refused")]
    public async Task Sabnzbd_does_not_take_an_answer_it_does_not_recognise_for_itself(int status, string body, string detail)
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api", body, (HttpStatusCode)status);

        var (ok, text) = await new SabnzbdPort(http).TestAsync(Connection(DownloadClientKinds.Sabnzbd));

        Assert.False(ok);
        Assert.Contains(detail, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("misc")]
    [InlineData("categories")]
    public async Task Sabnzbd_reads_no_folders_when_either_of_its_two_answers_is_refused(string refused)
    {
        var http = new FakeManagerHttp().Route(HttpMethod.Get, "/api", request =>
            FakeManagerHttp.Response(request.Uri.Query.Contains("section=" + refused, StringComparison.Ordinal) ? HttpStatusCode.InternalServerError : HttpStatusCode.OK, """{"config":{"misc":{},"categories":[]}}"""));

        var folders = await new SabnzbdPort(http).ReadFoldersAsync(Connection(DownloadClientKinds.Sabnzbd, apiKey: "k"));

        Assert.Equal(DownloadClientFolders.Empty, folders);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Sabnzbd_asks_with_an_empty_key_when_none_is_saved()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api", "{}");

        await new SabnzbdPort(http).ReadFoldersAsync(Connection(DownloadClientKinds.Sabnzbd));

        Assert.All(http.Requests, request => Assert.Contains("apikey=&", request.Uri.Query + "&", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(200, "<html>not nzbget</html>")]
    [InlineData(200, "[]")]
    [InlineData(200, """{"result":"fine"}""")]
    [InlineData(500, "")]
    public async Task Nzbget_does_not_take_an_answer_it_does_not_recognise_for_itself(int status, string body)
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Post, "/jsonrpc", body, (HttpStatusCode)status);

        var (ok, text) = await new NzbgetPort(http).TestAsync(Connection(DownloadClientKinds.Nzbget));

        Assert.False(ok);
        Assert.Contains(NotTheClient, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nzbget_reads_no_folders_from_a_refused_answer()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Post, "/jsonrpc", "", HttpStatusCode.InternalServerError);

        Assert.Equal(DownloadClientFolders.Empty, await new NzbgetPort(http).ReadFoldersAsync(Connection(DownloadClientKinds.Nzbget)));
    }

    [Theory]
    [InlineData(200, "<html>not transmission</html>")]
    [InlineData(200, "[]")]
    [InlineData(200, """{"result":"duplicate torrent"}""")]
    [InlineData(200, """{"result":5}""")]
    [InlineData(500, "")]
    public async Task Transmission_does_not_take_an_answer_it_does_not_recognise_for_itself(int status, string body)
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Post, "/transmission/rpc", body, (HttpStatusCode)status);

        var (ok, text) = await new TransmissionPort(http).TestAsync(Connection(DownloadClientKinds.Transmission));

        Assert.False(ok);
        Assert.Contains(NotTheClient, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transmission_reads_no_folders_from_a_refused_answer()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Post, "/transmission/rpc", "", HttpStatusCode.InternalServerError);

        Assert.Equal(DownloadClientFolders.Empty, await new TransmissionPort(http).ReadFoldersAsync(Connection(DownloadClientKinds.Transmission)));
    }

    [Fact]
    public async Task QBittorrent_says_it_is_not_qbittorrent_when_the_preferences_are_refused_after_a_good_login()
    {
        var http = new FakeManagerHttp()
            .Json(HttpMethod.Post, "/api/v2/auth/login", "Ok.")
            .Json(HttpMethod.Get, "/api/v2/app/preferences", "", HttpStatusCode.InternalServerError);

        var (ok, text) = await new QBittorrentPort(http).TestAsync(Connection(DownloadClientKinds.QBittorrent, username: "a", password: "b"));

        Assert.False(ok);
        Assert.Contains(NotTheClient, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QBittorrent_logs_in_with_empty_credentials_when_none_are_saved_and_reads_no_folders_when_refused()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Post, "/api/v2/auth/login", "Fails.");

        var folders = await new QBittorrentPort(http).ReadFoldersAsync(Connection(DownloadClientKinds.QBittorrent));

        Assert.Equal(DownloadClientFolders.Empty, folders);
        Assert.Equal("username=&password=", Assert.Single(http.Requests).Body);
    }

    [Theory]
    [InlineData(200, "<html>not deluge</html>")]
    [InlineData(200, "[]")]
    [InlineData(200, """{"result":false}""")]
    [InlineData(200, """{"result":"yes"}""")]
    [InlineData(500, "")]
    public async Task Deluge_refuses_a_login_it_does_not_recognise_as_accepted(int status, string body)
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Post, "/json", body, (HttpStatusCode)status);

        var (ok, text) = await new DelugePort(http).TestAsync(Connection(DownloadClientKinds.Deluge, password: "pw"));

        Assert.False(ok);
        Assert.Contains("password was refused", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deluge_logs_in_with_an_empty_password_when_none_is_saved_and_sends_no_cookie_when_the_login_gave_none()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Post, "/json", """{"result":true,"error":null,"id":1}""");

        var folders = await new DelugePort(http).ReadFoldersAsync(Connection(DownloadClientKinds.Deluge));

        Assert.NotNull(folders);
        Assert.All(http.Requests.Skip(1), request => Assert.False(request.Headers.ContainsKey("Cookie")));
        Assert.Contains("\"params\": [\"\"]", http.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deluge_sends_the_session_cookie_its_login_gave_on_every_later_call()
    {
        var http = new FakeManagerHttp().Route(
            HttpMethod.Post, "/json", _ => FakeManagerHttp.Response(HttpStatusCode.OK, """{"result":true,"error":null,"id":1}""", ("Set-Cookie", "_session_id=abc123; Path=/; HttpOnly")));

        await new DelugePort(http).ReadFoldersAsync(Connection(DownloadClientKinds.Deluge, password: "pw"));

        Assert.All(http.Requests.Skip(1), request => Assert.Equal("_session_id=abc123", request.Headers["Cookie"]));
        Assert.NotEmpty(http.Requests.Skip(1));
    }

    [Fact]
    public async Task A_response_body_that_fails_while_it_is_read_is_an_unreachable_client()
    {
        var http = new FakeManagerHttp().Route(
            HttpMethod.Get, "/api", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenBody() });
        var client = new DownloadClientHttpClient("http://client.local", http);

        var error = await Assert.ThrowsAsync<DownloadClientUnreachableException>(() => client.SendAsync(HttpMethod.Get, "/api"));

        Assert.StartsWith("The connection failed: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_with_no_leading_slash_and_a_base_with_one_are_joined_with_one()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Get, "/api/v2/app/version", "5.0.0");
        var client = new DownloadClientHttpClient("http://client.local/", http);

        await client.SendAsync(HttpMethod.Get, "api/v2/app/version", [new("a", "1")]);

        Assert.Equal("/api/v2/app/version?a=1", Assert.Single(http.Requests).PathAndQuery);
    }

    private sealed class BrokenBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new HttpRequestException("The response ended prematurely.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
