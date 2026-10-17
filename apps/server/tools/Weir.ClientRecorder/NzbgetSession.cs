using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Weir.ArrRecorder;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ClientRecorder;

/// <summary>
/// NZBGet. It starts with the published user name and password, and its default categories have no folder of their own; the session gives
/// one a folder through the client's own settings (saved, then the program reloads), so Weir is recorded reading one.
/// </summary>
internal sealed class NzbgetSession(ClientSettings settings) : ClientSession(settings)
{
    private const string User = "nzbget";
    private const string Password = "tegbzn6789";
    private const string CategoryFolder = "/downloads/complete/category-one";

    private int _id;

    protected override async Task<ClientSetup> SetUpAsync()
    {
        string version = string.Empty;
        await Wait.UntilAsync("NZBGet to answer", TimeSpan.FromMinutes(3), async () =>
        {
            version = (string?)(await RpcAsync("version", []).ConfigureAwait(false)) ?? string.Empty;
            return version.Length > 0;
        }).ConfigureAwait(false);

        await TryAsync("a folder for the first category", async () =>
        {
            var options = (await RpcAsync("loadconfig", []).ConfigureAwait(false))?.AsArray() ?? throw new RecorderException("NZBGet sent no options");
            foreach (var option in options.OfType<JsonObject>().Where(option => (string?)option["Name"] == "Category1.DestDir"))
            {
                option["Value"] = CategoryFolder;
            }

            Expect((bool?)await RpcAsync("saveconfig", [options.DeepClone()]).ConfigureAwait(false) == true, "NZBGet should save its settings");
            await RpcAsync("reload", []).ConfigureAwait(false);
            await Wait.UntilAsync("NZBGet to run with the new folder", TimeSpan.FromMinutes(2), async () =>
                (await RpcAsync("config", []).ConfigureAwait(false))?.AsArray().OfType<JsonObject>().Any(option => (string?)option["Name"] == "Category1.DestDir" && (string?)option["Value"] == CategoryFolder) == true).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return new ClientSetup(
            Settings.Version ?? version.TrimStart('v'),
            Connection(DownloadClientKinds.Nzbget, Settings.Url, User, Password, null),
            Connection(DownloadClientKinds.Nzbget, Settings.Url, User, "not-the-password", null),
            [Password]);
    }

    protected override IDownloadClientPort CreatePort(IManagerHttpHandlerFactory transport) => new NzbgetPort(transport);

    protected override async Task RecordAsync()
    {
        await TestStepAsync("test-wrong-password", "The connection test with a password NZBGet does not know.", Prepared.Wrong, expectConnected: false).ConfigureAwait(false);
        await TestStepAsync("test", "The connection test with the right user name and password.", Prepared.Right, expectConnected: true).ConfigureAwait(false);
        await FoldersStepAsync(
            "folders",
            "Reading the completed folder and the categories of an NZBGet where one category has a folder of its own.",
            Prepared.Right,
            folders => folders.CompletedFolder is { Length: > 0 } && folders.CategoryFolders.Count >= 1,
            "its completed folder and the category with a folder").ConfigureAwait(false);
        await FoldersStepAsync(
            "folders-wrong-password", "Reading the folders with a password NZBGet does not know.", Prepared.Wrong, folders => folders == DownloadClientFolders.Empty, "no folders").ConfigureAwait(false);
    }

    /// <summary>One JSON-RPC call by the recorder, signed in with the published user. Not part of any recording.</summary>
    private async Task<JsonNode?> RpcAsync(string method, JsonNode?[] parameters)
    {
        var body = new JsonObject { ["method"] = method, ["params"] = new JsonArray(parameters), ["id"] = ++_id };
        var request = new HttpRequestMessage(HttpMethod.Post, "/jsonrpc") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));
        var reply = await SendAsync(request, method).ConfigureAwait(false);
        var answer = JsonNode.Parse(reply.Text);
        return answer?["result"];
    }
}
