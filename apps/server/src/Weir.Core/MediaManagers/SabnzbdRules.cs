using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// SABnzbd's <c>GET /api?mode=get_config</c> JSON, read with no I/O. <c>section=misc</c> answers
/// <c>{"config":{"misc":{"complete_dir":"..."}}}</c>; <c>section=categories</c> answers
/// <c>{"config":{"categories":[{"name":"...","dir":"..."},...]}}</c>.
/// </summary>
public static class SabnzbdRules
{
    /// <summary>The base completed-downloads folder, from <c>section=misc</c>.</summary>
    public static string? ParseCompleteDir(WireValue? miscConfig) =>
        Config(miscConfig)?.Get("misc") is WireObject misc ? ManagerValues.Text(misc.Get("complete_dir")) : null;

    /// <summary>
    /// Each category's own folder, from <c>section=categories</c>. A category's <c>dir</c> is relative to
    /// <paramref name="completeDir"/> unless it already looks absolute, in which case it replaces it outright.
    /// The default category (<c>"*"</c>) is not a folder suggestion of its own — its downloads are already
    /// covered by <see cref="ParseCompleteDir"/>.
    /// </summary>
    public static List<DownloadClientCategoryFolder> ParseCategoryFolders(WireValue? categoriesConfig, string? completeDir)
    {
        if (Config(categoriesConfig)?.Get("categories") is not WireArray categories)
        {
            return [];
        }

        var folders = new List<DownloadClientCategoryFolder>();
        foreach (var row in categories.Items.OfType<WireObject>())
        {
            var name = ManagerValues.Text(row.Get("name"));
            var dir = ManagerValues.Text(row.Get("dir"));
            if (name is null || dir is null || name == "*")
            {
                continue;
            }

            // A folder that climbs out with '..' is the client's own text, not a place Weir will suggest.
            if (DownloadClientPaths.ClimbsOut(dir) || JoinUnderComplete(completeDir, dir) is not { } folder)
            {
                continue;
            }

            folders.Add(new DownloadClientCategoryFolder(name, folder));
        }

        return folders;
    }

    private static WireObject? Config(WireValue? payload) => payload is WireObject dict && dict.Get("config") is WireObject config ? config : null;

    private static string? JoinUnderComplete(string? completeDir, string relativeOrAbsolute) =>
        DownloadClientPaths.IsAbsolute(relativeOrAbsolute) || string.IsNullOrEmpty(completeDir)
            ? relativeOrAbsolute
            : DownloadClientPaths.Join(completeDir, relativeOrAbsolute).Path;
}
