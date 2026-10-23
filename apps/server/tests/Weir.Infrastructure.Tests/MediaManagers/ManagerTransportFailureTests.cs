using System.Net;
using System.Security.Authentication;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// What the manager and download-client clients do when the network or the answer is not what a real client sends: a body that
/// breaks while it is read, an address that is not safe, a status nobody listed, and a download client that cannot be reached.
/// </summary>
public sealed class ManagerTransportFailureTests
{
    /// <summary>Handlers a test writes as a function from request to answer, so the failure is exactly the one named.</summary>
    private sealed class Handlers(Func<HttpRequestMessage, HttpResponseMessage> respond) : IManagerHttpHandlerFactory
    {
        public HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local) => new Answering(respond);

        private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }
    }

    /// <summary>A body that fails part-way, as a connection dropped mid-answer does.</summary>
    private sealed class BrokenBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("connection reset");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static Handlers Throwing(Exception exception) => new Handlers(_ => throw exception);

    private static Handlers Answering(HttpStatusCode status, string body = "") =>
        new Handlers(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static DownloadClientConnection Connection(string kind) => new(kind, "Main", "http://client.local", "user", "pass", "key", 1);

    [Fact]
    public async Task A_manager_answer_that_breaks_while_it_is_read_is_unreachable()
    {
        var handlers = new Handlers(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenBody() });
        var client = new MediaManagerHttpClient("http://manager.local", "key", handlers);

        await Assert.ThrowsAsync<MediaManagerUnreachableException>(() => client.GetJsonAsync("/api/v3/queue"));
    }

    [Fact]
    public async Task A_manager_certificate_problem_is_named_as_one()
    {
        var failure = new HttpRequestException("ssl", new AuthenticationException("untrusted"));
        var client = new MediaManagerHttpClient("http://manager.local", "key", Throwing(failure));

        var error = await Assert.ThrowsAsync<MediaManagerUnreachableException>(() => client.GetJsonAsync("/api/v3/queue"));

        Assert.Contains("a certificate problem", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_success_status_the_caller_did_not_accept_is_refused()
    {
        var client = new MediaManagerHttpClient("http://manager.local", "key", Answering(HttpStatusCode.Accepted, "{}"));

        var error = await Assert.ThrowsAsync<MediaManagerHttpException>(() => client.GetJsonAsync("/api/v3/queue"));

        Assert.Equal("unexpected HTTP 202", error.Message);
    }

    [Fact]
    public async Task An_empty_answer_from_a_get_is_nothing_not_an_error()
    {
        var client = new MediaManagerHttpClient("http://manager.local", "key", Answering(HttpStatusCode.OK));

        Assert.Null(await client.GetJsonAsync("/api/v3/queue"));
    }

    [Theory]
    [InlineData("ftp://client.local")]
    [InlineData("http://user:pass@client.local")]
    public void A_download_client_address_that_is_not_plain_http_is_refused(string baseUrl)
    {
        Assert.Throws<DownloadClientHttpException>(() => new DownloadClientHttpClient(baseUrl, Answering(HttpStatusCode.OK)));
    }

    [Fact]
    public void A_download_client_path_with_a_scheme_is_refused()
    {
        var client = new DownloadClientHttpClient("http://client.local", Answering(HttpStatusCode.OK));

        Assert.Throws<DownloadClientHttpException>(() => client.Url("http://elsewhere.example/api"));
    }

    [Fact]
    public async Task A_download_client_answer_that_breaks_while_it_is_read_is_unreachable()
    {
        var handlers = new Handlers(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenBody() });
        var client = new DownloadClientHttpClient("http://client.local", handlers);

        await Assert.ThrowsAsync<DownloadClientUnreachableException>(() => client.SendAsync(HttpMethod.Get, "/api"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("<html>not json</html>", false)]
    [InlineData("{\"ok\": true}", true)]
    public async Task A_download_client_answer_is_JSON_only_when_it_parses(string body, bool parses)
    {
        var client = new DownloadClientHttpClient("http://client.local", Answering(HttpStatusCode.OK, body));

        var response = await client.SendAsync(HttpMethod.Get, "/api");

        Assert.Equal(parses, response.Json() is not null);
    }

    [Theory]
    [InlineData(DownloadClientKinds.Sabnzbd)]
    [InlineData(DownloadClientKinds.Nzbget)]
    [InlineData(DownloadClientKinds.QBittorrent)]
    [InlineData(DownloadClientKinds.Deluge)]
    [InlineData(DownloadClientKinds.Transmission)]
    public async Task A_download_client_that_cannot_be_reached_says_so_and_has_no_folders(string kind)
    {
        var handlers = Throwing(new HttpRequestException("refused"));
        IDownloadClientPort port = kind switch
        {
            DownloadClientKinds.Sabnzbd => new SabnzbdPort(handlers),
            DownloadClientKinds.Nzbget => new NzbgetPort(handlers),
            DownloadClientKinds.QBittorrent => new QBittorrentPort(handlers),
            DownloadClientKinds.Deluge => new DelugePort(handlers),
            _ => new TransmissionPort(handlers),
        };

        var (ok, detail) = await port.TestAsync(Connection(kind));
        var folders = await port.ReadFoldersAsync(Connection(kind));

        Assert.False(ok);
        Assert.Equal(DownloadClientDialectRules.Unreachable(Connection(kind)), detail);
        Assert.Equal(DownloadClientFolders.Empty, folders);
    }

    [Fact]
    public async Task QBittorrent_has_no_folders_when_it_refuses_the_login()
    {
        var port = new QBittorrentPort(Answering(HttpStatusCode.Unauthorized));

        Assert.Equal(DownloadClientFolders.Empty, await port.ReadFoldersAsync(Connection(DownloadClientKinds.QBittorrent)));
    }

    [Fact]
    public async Task Deluge_has_no_folders_when_it_refuses_the_password()
    {
        var port = new DelugePort(Answering(HttpStatusCode.OK, "{\"result\": false}"));

        Assert.Equal(DownloadClientFolders.Empty, await port.ReadFoldersAsync(Connection(DownloadClientKinds.Deluge)));
    }

    [Fact]
    public async Task Transmission_that_refuses_without_naming_a_session_has_no_folders()
    {
        var port = new TransmissionPort(Answering(HttpStatusCode.Conflict));

        Assert.Equal(DownloadClientFolders.Empty, await port.ReadFoldersAsync(Connection(DownloadClientKinds.Transmission)));
    }
}
