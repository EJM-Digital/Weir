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
    /// A category with no folder of its own saves where the default category (<c>"*"</c>) does: its folder when it has one,
    /// otherwise <paramref name="completeDir"/> itself (<c>get_complete_directory</c> in SABnzbd's postproc.py,
    /// https://raw.githubusercontent.com/sabnzbd/sabnzbd/develop/sabnzbd/postproc.py). A folder ending in <c>*</c> only
    /// switches off the per-job folder, so the asterisk is not part of the folder
    /// (https://sabnzbd.org/wiki/configuration/5.1/categories). The default category is not a folder suggestion of its own.
    /// </summary>
    public static List<DownloadClientCategoryFolder> ParseCategoryFolders(WireValue? categoriesConfig, string? completeDir)
    {
        if (Config(categoriesConfig)?.Get("categories") is not WireArray categories)
        {
            return [];
        }

        var rows = categories.Items.OfType<WireObject>().ToList();
        var defaultDir = rows.Where(row => ManagerValues.Text(row.Get("name")) == DefaultCategory)
            .Select(row => FolderText(row.Get("dir"))).FirstOrDefault(dir => dir.Length > 0) ?? string.Empty;

        var folders = new List<DownloadClientCategoryFolder>();
        foreach (var row in rows)
        {
            var name = ManagerValues.Text(row.Get("name"));
            if (name is null || name == DefaultCategory)
            {
                continue;
            }

            var dir = FolderText(row.Get("dir"));
            if (dir.Length == 0)
            {
                dir = defaultDir;
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

    private const string DefaultCategory = "*";

    private static string FolderText(WireValue? dir) => (ManagerValues.Text(dir) ?? string.Empty).TrimEnd('*');

    private static WireObject? Config(WireValue? payload) => payload is WireObject dict && dict.Get("config") is WireObject config ? config : null;

    private static string? JoinUnderComplete(string? completeDir, string relativeOrAbsolute)
    {
        if (relativeOrAbsolute.Length == 0)
        {
            return string.IsNullOrEmpty(completeDir) ? null : completeDir;
        }

        return DownloadClientPaths.IsAbsolute(relativeOrAbsolute) || string.IsNullOrEmpty(completeDir)
            ? relativeOrAbsolute
            : DownloadClientPaths.Join(completeDir, relativeOrAbsolute).Path;
    }
}
