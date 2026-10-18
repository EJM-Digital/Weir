using System.Globalization;
using System.Text.RegularExpressions;
using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// NZBGet's JSON-RPC <c>config</c> method, read with no I/O: a flat array of
/// <c>{"Name": "...", "Value": "..."}</c> option rows under <c>result</c>, holding the base completed folder
/// (<c>DestDir</c>) and each category's own (<c>CategoryN.Name</c> paired with <c>CategoryN.DestDir</c>).
/// A category with no <c>DestDir</c> saves into <c>DestDir/&lt;category&gt;</c> when <c>AppendCategoryDir</c> is on (its default) and into
/// <c>DestDir</c> itself when it is off, as nzbget.conf documents for <c>CategoryN.DestDir</c>:
/// https://raw.githubusercontent.com/nzbget/nzbget/develop/nzbget.conf
/// </summary>
public static partial class NzbgetRules
{
    [GeneratedRegex(@"^Category(\d+)\.(Name|DestDir)$")]
    private static partial Regex CategoryOptionKey();

    public static DownloadClientFolders ParseConfig(WireValue? response)
    {
        var options = FlattenOptions(response);
        var destDir = options.GetValueOrDefault("DestDir");
        var baseFolder = string.IsNullOrEmpty(destDir) ? null : destDir;
        return new DownloadClientFolders(baseFolder, ParseCategories(options, baseFolder));
    }

    private static List<DownloadClientCategoryFolder> ParseCategories(Dictionary<string, string> options, string? baseFolder)
    {
        var byIndex = new SortedDictionary<int, (string? Name, string? DestDir)>();
        foreach (var (key, value) in options)
        {
            var match = CategoryOptionKey().Match(key);
            if (!match.Success)
            {
                continue;
            }

            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var entry = byIndex.GetValueOrDefault(index);
            byIndex[index] = match.Groups[2].Value == "Name" ? (value, entry.DestDir) : (entry.Name, value);
        }

        var appendCategoryDir = !string.Equals(options.GetValueOrDefault("AppendCategoryDir"), "no", StringComparison.OrdinalIgnoreCase);
        var folders = new List<DownloadClientCategoryFolder>();
        foreach (var (name, destDir) in byIndex.Values)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (CategoryFolder(name, destDir, baseFolder, appendCategoryDir) is { } folder)
            {
                folders.Add(new DownloadClientCategoryFolder(name, folder));
            }
        }

        return folders;
    }

    private static string? CategoryFolder(string name, string? destDir, string? baseFolder, bool appendCategoryDir)
    {
        if (!string.IsNullOrEmpty(destDir))
        {
            return destDir;
        }

        if (string.IsNullOrEmpty(baseFolder))
        {
            return null;
        }

        return appendCategoryDir ? DownloadClientPaths.Join(baseFolder, name).Path : baseFolder;
    }

    private static Dictionary<string, string> FlattenOptions(WireValue? response)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (response is not WireObject dict || dict.Get("result") is not WireArray result)
        {
            return options;
        }

        foreach (var row in result.Items.OfType<WireObject>())
        {
            if (ManagerValues.Text(row.Get("Name")) is { } name)
            {
                options[name] = ManagerValues.Text(row.Get("Value")) ?? string.Empty;
            }
        }

        return options;
    }
}
