using System.Text;
using System.Text.Json.Nodes;
using Weir.ArrRecorder;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ClientRecorder;

/// <summary>
/// Deluge. The Web UI starts with the published password <c>deluge</c> and the label plugin off. The session changes the password, makes
/// sure the Web UI is connected to the daemon, and records what Weir reads before and after the plugin is on and a label has a folder.
/// </summary>
internal sealed class DelugeSession(ClientSettings settings) : ClientSession(settings)
{
    private const string PublishedPassword = "deluge";
    private const string Password = "recorder-deluge-secret";
    private const string CompletedFolder = "/downloads/complete";

    private int _id;
    private string _cookie = string.Empty;

    protected override async Task<ClientSetup> SetUpAsync()
    {
        await Wait.UntilAsync("Deluge's Web UI to let the published password in", TimeSpan.FromMinutes(3), async () =>
        {
            var login = await RpcAsync("auth.login", [PublishedPassword], signedIn: false).ConfigureAwait(false);
            return (bool?)login.Result == true;
        }).ConfigureAwait(false);

        await TryAsync("a connection from the Web UI to the daemon", async () =>
        {
            if ((bool?)(await RpcAsync("web.connected", []).ConfigureAwait(false)).Result == true)
            {
                return;
            }

            var hosts = (await RpcAsync("web.get_hosts", []).ConfigureAwait(false)).Result?.AsArray();
            var hostId = (string?)hosts?.FirstOrDefault()?[0] ?? throw new RecorderException("the Web UI knows no daemon to connect to");
            await RpcAsync("web.connect", [hostId]).ConfigureAwait(false);
            await Wait.UntilAsync("the Web UI to connect", TimeSpan.FromMinutes(1), async () => (bool?)(await RpcAsync("web.connected", []).ConfigureAwait(false)).Result == true).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await TryAsync("a password of its own", () => RpcAsync("auth.change_password", [PublishedPassword, Password])).ConfigureAwait(false);
        await TryAsync("a folder for completed downloads", () => RpcAsync(
            "core.set_config", [new JsonObject { ["move_completed"] = true, ["move_completed_path"] = CompletedFolder }])).ConfigureAwait(false);

        var version = string.Empty;
        await TryAsync("the daemon's version", async () => version = (string?)(await RpcAsync("daemon.info", []).ConfigureAwait(false)).Result ?? string.Empty).ConfigureAwait(false);
        return new ClientSetup(
            Settings.Version ?? (version.Length > 0 ? version : throw new InvalidOperationException("Deluge did not say its version; set RECORDER_VERSION.")),
            Connection(DownloadClientKinds.Deluge, Settings.Url, null, Password, null),
            Connection(DownloadClientKinds.Deluge, Settings.Url, null, "not-the-password", null),
            [Password]);
    }

    protected override IDownloadClientPort CreatePort(IManagerHttpHandlerFactory transport) => new DelugePort(transport);

    protected override async Task RecordAsync()
    {
        await TestStepAsync("test-wrong-password", "The connection test with a password Deluge's Web UI does not know.", Prepared.Wrong, expectConnected: false).ConfigureAwait(false);
        await TestStepAsync("test", "The connection test with the right password.", Prepared.Right, expectConnected: true).ConfigureAwait(false);

        await FoldersStepAsync(
            "folders-without-labels",
            "Reading the folders of a Deluge whose label plugin is off.",
            Prepared.Right,
            folders => folders.CompletedFolder == CompletedFolder && folders.CategoryFolders.Count == 0,
            $"{CompletedFolder} and no labels").ConfigureAwait(false);

        await TryAsync("the label plugin", () => RpcAsync("core.enable_plugin", ["Label"])).ConfigureAwait(false);
        await TryAsync("a label with a folder of its own", async () =>
        {
            await RpcAsync("label.add", ["tv-recorder"]).ConfigureAwait(false);
            await RpcAsync("label.set_options", ["tv-recorder", new JsonObject { ["move_completed"] = true, ["move_completed_path"] = "/downloads/complete/tv" }]).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await FoldersStepAsync(
            "folders",
            "Reading the folders of a Deluge whose label plugin is on and has a label with a folder of its own.",
            Prepared.Right,
            folders => folders.CompletedFolder == CompletedFolder && folders.CategoryFolders.Count == 1,
            $"{CompletedFolder} and the label tv-recorder").ConfigureAwait(false);

        await TryAsync("the Web UI disconnected from the daemon", () => RpcAsync("web.disconnect", [])).ConfigureAwait(false);
        await FoldersStepAsync(
            "folders-daemon-not-connected",
            "Reading the folders while the Web UI is not connected to the daemon.",
            Prepared.Right,
            _ => true,
            "whatever Weir makes of it").ConfigureAwait(false);
    }

    /// <summary>One Web UI JSON-RPC call by the recorder, with the session cookie once signed in. Not part of any recording.</summary>
    private async Task<(JsonNode? Result, string? Cookie)> RpcAsync(string method, JsonNode?[] parameters, bool signedIn = true)
    {
        var body = new JsonObject { ["method"] = method, ["params"] = new JsonArray(parameters), ["id"] = ++_id };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/json") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (signedIn && _cookie.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        }

        var reply = await SendAsync(request, method).ConfigureAwait(false);
        if (reply.Cookie is not null)
        {
            _cookie = reply.Cookie;
        }

        var answer = JsonNode.Parse(reply.Text);
        if (answer?["error"] is JsonObject error)
        {
            throw new RecorderException($"Deluge's {method} answered an error: {error.ToJsonString()}");
        }

        return (answer?["result"], reply.Cookie);
    }
}
