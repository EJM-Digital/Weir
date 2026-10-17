using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Weir.ArrRecorder;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ClientRecorder;

/// <summary>
/// SABnzbd. It writes its API key to <c>sabnzbd.ini</c> in its config folder on first start; the session reads it from there, then sets a
/// completed folder and categories (one with a folder below it, one with a folder elsewhere) through the client's own API.
/// </summary>
internal sealed partial class SabnzbdSession(ClientSettings settings) : ClientSession(settings)
{
    private const string CompleteDir = "/downloads/complete";
    private const string WrongKey = "0123456789abcdef0123456789abcdef";

    [GeneratedRegex(@"^(?<name>api_key|nzb_key)\s*=\s*(?<value>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex KeyLines();

    protected override async Task<ClientSetup> SetUpAsync()
    {
        var config = Settings.ConfigFolder ?? throw new InvalidOperationException("RECORDER_CONFIG_DIR is not set; it is SABnzbd's config folder, where sabnzbd.ini appears.");
        var keys = new Dictionary<string, string>();
        await Wait.UntilAsync("SABnzbd to write its keys", TimeSpan.FromMinutes(3), () =>
        {
            var file = Path.Combine(config, "sabnzbd.ini");
            keys = File.Exists(file) ? KeyLines().Matches(File.ReadAllText(file)).ToDictionary(line => line.Groups["name"].Value, line => line.Groups["value"].Value) : [];
            return Task.FromResult(keys.ContainsKey("api_key") && keys.ContainsKey("nzb_key"));
        }).ConfigureAwait(false);
        var key = keys["api_key"];

        string version = string.Empty;
        await Wait.UntilAsync("SABnzbd to answer", TimeSpan.FromMinutes(3), async () =>
        {
            version = (string?)JsonNode.Parse((await Api("mode=version&output=json").ConfigureAwait(false)).Text)?["version"] ?? string.Empty;
            return version.Length > 0;
        }).ConfigureAwait(false);

        await TryAsync("a completed folder", () => Api($"mode=set_config&section=misc&keyword=complete_dir&value={CompleteDir}&output=json&apikey={key}")).ConfigureAwait(false);
        await TryAsync("a category with a folder below the completed one", () => Api($"mode=set_config&section=categories&name=tv-recorder&dir=tv&output=json&apikey={key}")).ConfigureAwait(false);
        await TryAsync("a category with a folder elsewhere", () => Api($"mode=set_config&section=categories&name=elsewhere-recorder&dir=/media/elsewhere&output=json&apikey={key}")).ConfigureAwait(false);

        return new ClientSetup(
            Settings.Version ?? version,
            Connection(DownloadClientKinds.Sabnzbd, Settings.Url, null, null, key),
            Connection(DownloadClientKinds.Sabnzbd, Settings.Url, null, null, WrongKey),
            [key, keys["nzb_key"]]);
    }

    protected override IDownloadClientPort CreatePort(IManagerHttpHandlerFactory transport) => new SabnzbdPort(transport);

    protected override async Task RecordAsync()
    {
        await TestStepAsync("test-wrong-key", "The connection test with an API key SABnzbd does not know.", Prepared.Wrong, expectConnected: false).ConfigureAwait(false);
        await TestStepAsync("test", "The connection test with the right API key.", Prepared.Right, expectConnected: true).ConfigureAwait(false);
        await FoldersStepAsync(
            "folders",
            "Reading the completed folder and the categories of a SABnzbd that has two of its own.",
            Prepared.Right,
            folders => folders.CompletedFolder == CompleteDir && folders.CategoryFolders.Count >= 2,
            $"{CompleteDir} and the categories with a folder").ConfigureAwait(false);
        await FoldersStepAsync(
            "folders-wrong-key", "Reading the folders with an API key SABnzbd does not know.", Prepared.Wrong, folders => folders == DownloadClientFolders.Empty, "no folders").ConfigureAwait(false);
    }

    private Task<SetUpReply> Api(string query) => SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api?" + query), "SABnzbd's API");
}
