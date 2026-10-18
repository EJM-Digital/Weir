using System.Net;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The cases of the five download-client dialects (#768) that no recording can hold, against a scripted <see cref="FakeManagerHttp"/>: a
/// client that cannot be reached, a login answered 204 or 401, no sign-in saved, a handshake that never completes. What a real client
/// answers is replayed from its recordings (<c>Recorded/</c>).
/// </summary>
public sealed class DownloadClientPortsTests
{
    private static DownloadClientConnection Connection(
        string kind, string? username = null, string? password = null, string? apiKey = null) =>
        new(kind, "Main", "http://client.local", username, password, apiKey, 1);

    // --- SABnzbd --------------------------------------------------------------------------------

    [Fact]
    public async Task Sabnzbd_read_folders_is_empty_not_thrown_when_unreachable()
    {
        var http = new FakeManagerHttp().Throw(HttpMethod.Get, "/api", new HttpRequestException("refused"));
        var port = new SabnzbdPort(http);

        var folders = await port.ReadFoldersAsync(Connection(DownloadClientKinds.Sabnzbd, apiKey: "k"));

        Assert.Equal(DownloadClientFolders.Empty, folders);
    }

    // --- NZBGet -----------------------------------------------------------------------------------

    [Fact]
    public async Task Nzbget_sends_no_authorization_header_when_no_credential_is_saved()
    {
        var http = new FakeManagerHttp().Json(HttpMethod.Post, "/jsonrpc", """{"result":[]}""");
        var port = new NzbgetPort(http);

        await port.ReadFoldersAsync(Connection(DownloadClientKinds.Nzbget));

        Assert.False(Assert.Single(http.Requests).Headers.ContainsKey("Authorization"));
    }

    // --- qBittorrent ------------------------------------------------------------------------------

    [Fact]
    public async Task QBittorrent_5_accepts_a_204_login_and_sends_its_QBT_SID_cookie_on_later_calls()
    {
        var http = new FakeManagerHttp()
            .Route(HttpMethod.Post, "/api/v2/auth/login", _ => FakeManagerHttp.Response(HttpStatusCode.NoContent, null, ("Set-Cookie", "QBT_SID_8081=xyz789; HttpOnly; SameSite=Strict; path=/")))
            .Json(HttpMethod.Get, "/api/v2/torrents/categories", """{"tv-sonarr":{"name":"tv-sonarr","savePath":"/downloads/complete/tv"}}""")
            .Json(HttpMethod.Get, "/api/v2/app/preferences", """{"save_path":"/downloads/complete"}""");
        var port = new QBittorrentPort(http);

        var (ok, _) = await port.TestAsync(Connection(DownloadClientKinds.QBittorrent, username: "deluno", password: "right"));
        var folders = await port.ReadFoldersAsync(Connection(DownloadClientKinds.QBittorrent, username: "deluno", password: "right"));

        Assert.True(ok);
        Assert.Equal("/downloads/complete", folders.CompletedFolder);
        var laterRequests = http.Requests.Where(request => request.Uri.AbsolutePath != "/api/v2/auth/login").ToList();
        Assert.Equal(3, laterRequests.Count);
        Assert.All(laterRequests, request => Assert.Equal("QBT_SID_8081=xyz789", request.Headers["Cookie"]));
    }

    [Fact]
    public async Task QBittorrent_refuses_a_login_answered_401()
    {
        var http = new FakeManagerHttp().Route(HttpMethod.Post, "/api/v2/auth/login", _ => FakeManagerHttp.Response(HttpStatusCode.Unauthorized));
        var port = new QBittorrentPort(http);

        var (ok, detail) = await port.TestAsync(Connection(DownloadClientKinds.QBittorrent, username: "admin", password: "wrong"));

        Assert.False(ok);
        Assert.Contains("username or password was refused", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QBittorrent_accepts_a_204_login_that_sets_no_session_cookie_and_reads_without_one()
    {
        var http = new FakeManagerHttp()
            .Route(HttpMethod.Post, "/api/v2/auth/login", _ => FakeManagerHttp.Response(HttpStatusCode.NoContent))
            .Json(HttpMethod.Get, "/api/v2/app/preferences", """{"save_path":"/downloads/complete"}""");
        var port = new QBittorrentPort(http);

        var (ok, _) = await port.TestAsync(Connection(DownloadClientKinds.QBittorrent, username: "deluno", password: "right"));

        Assert.True(ok);
        Assert.False(http.RequestsTo(HttpMethod.Get, "/api/v2/app/preferences").Single().Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task QBittorrent_sends_its_login_without_a_session_cookie()
    {
        var http = new FakeManagerHttp()
            .Route(HttpMethod.Post, "/api/v2/auth/login", _ => FakeManagerHttp.Response(HttpStatusCode.NoContent, null, ("Set-Cookie", "QBT_SID_8081=xyz789; HttpOnly")))
            .Json(HttpMethod.Get, "/api/v2/app/preferences", """{"save_path":"/downloads/complete"}""");
        var port = new QBittorrentPort(http);

        await port.TestAsync(Connection(DownloadClientKinds.QBittorrent, username: "deluno", password: "right"));
        await port.TestAsync(Connection(DownloadClientKinds.QBittorrent, username: "deluno", password: "right"));

        var logins = http.RequestsTo(HttpMethod.Post, "/api/v2/auth/login");
        Assert.Equal(2, logins.Count);
        Assert.All(logins, login => Assert.False(login.Headers.ContainsKey("Cookie")));
    }

    // --- Transmission ---------------------------------------------------------------------------

    [Fact]
    public async Task Transmission_gives_up_after_one_retry_when_the_second_answer_is_still_409()
    {
        var http = new FakeManagerHttp().Route(
            HttpMethod.Post, "/transmission/rpc", _ => FakeManagerHttp.Response(HttpStatusCode.Conflict, null, ("X-Transmission-Session-Id", "session-abc")));
        var port = new TransmissionPort(http);

        var folders = await port.ReadFoldersAsync(Connection(DownloadClientKinds.Transmission));

        Assert.Equal(DownloadClientFolders.Empty, folders);
        Assert.Equal(2, http.Requests.Count);
    }
}
