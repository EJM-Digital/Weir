using System.Text.Json.Nodes;
using Weir.ArrRecorder;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ClientRecorder;

/// <summary>
/// qBittorrent. A fresh container prints a temporary password for its <c>admin</c> user (the workflow hands it over as
/// <c>RECORDER_SETUP_PASSWORD</c>); the session signs in with it, sets a password of its own, a default save path and three categories
/// (one with a save path below the default, one with one elsewhere, one with none), then records what Weir does.
/// </summary>
internal sealed class QBittorrentSession(ClientSettings settings) : ClientSession(settings)
{
    private const string User = "admin";
    private const string Password = "recorder-qbittorrent-secret";
    private const string DefaultSavePath = "/downloads/complete";

    protected override async Task<ClientSetup> SetUpAsync()
    {
        var temporary = Settings.SetupPassword ?? throw new InvalidOperationException("RECORDER_SETUP_PASSWORD is not set; it is the temporary password qBittorrent printed.");
        string cookie = string.Empty;
        await Wait.UntilAsync("qBittorrent to let the temporary password in", TimeSpan.FromMinutes(3), async () =>
        {
            var reply = await LoginAsync(temporary).ConfigureAwait(false);
            cookie = reply.Cookie ?? string.Empty;
            return reply.Text.Trim() is "Ok." or "";
        }).ConfigureAwait(false);

        var version = (await Get("/api/v2/app/version", cookie).ConfigureAwait(false)).Trim().TrimStart('v');
        await TryAsync("a password of its own and a default save path", async () =>
        {
            var preferences = new JsonObject { ["web_ui_password"] = Password, ["save_path"] = DefaultSavePath }.ToJsonString();
            await Post("/api/v2/app/setPreferences", cookie, new Dictionary<string, string> { ["json"] = preferences }).ConfigureAwait(false);
        }).ConfigureAwait(false);
        foreach (var (name, savePath) in new[] { ("tv-recorder", "/downloads/complete/tv"), ("elsewhere-recorder", "/media/elsewhere"), ("default-recorder", string.Empty) })
        {
            await TryAsync($"the category {name}", () => Post(
                "/api/v2/torrents/createCategory", cookie, new Dictionary<string, string> { ["category"] = name, ["savePath"] = savePath })).ConfigureAwait(false);
        }

        return new ClientSetup(
            Settings.Version ?? version,
            Connection(DownloadClientKinds.QBittorrent, Settings.Url, User, Password, null),
            Connection(DownloadClientKinds.QBittorrent, Settings.Url, User, "not-the-password", null),
            [temporary, Password]);
    }

    protected override IDownloadClientPort CreatePort(IManagerHttpHandlerFactory transport) => new QBittorrentPort(transport);

    protected override async Task RecordAsync()
    {
        await TestStepAsync("test-wrong-password", "The connection test with a password qBittorrent does not know.", Prepared.Wrong, expectConnected: false).ConfigureAwait(false);
        await TestStepAsync("test", "The connection test with the right user name and password.", Prepared.Right, expectConnected: true).ConfigureAwait(false);
        await FoldersStepAsync(
            "folders",
            "Reading the default save path and the categories of a qBittorrent that has three.",
            Prepared.Right,
            folders => folders.CompletedFolder == DefaultSavePath && folders.CategoryFolders.Count >= 2,
            $"the default save path {DefaultSavePath} and its categories").ConfigureAwait(false);
    }

    private async Task<SetUpReply> LoginAsync(string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/auth/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = User, ["password"] = password }),
        };
        return await SendAsync(request, "qBittorrent's login").ConfigureAwait(false);
    }

    private async Task<string> Get(string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return (await SendAsync(request, $"GET {path}").ConfigureAwait(false)).Text;
    }

    private async Task Post(string path, string cookie, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        await SendAsync(request, $"POST {path}").ConfigureAwait(false);
    }
}
