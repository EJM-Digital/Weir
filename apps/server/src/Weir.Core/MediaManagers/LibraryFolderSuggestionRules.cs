namespace Weir.Core.MediaManagers;

/// <summary>
/// The folders a first library can start from when a media manager or download client is connected: which of a
/// download client's folders belongs to a media type, and where cleaned files go when nothing reports a folder for
/// them. Suggestions only. Nothing here creates or changes a library.
/// </summary>
public static class LibraryFolderSuggestionRules
{
    /// <summary>The folder cleaned files are written under, beside the watched folder's own parent.</summary>
    public const string ReadyFolderName = "Weir Ready";

    private static readonly string[] MovieCategoryWords = ["movie", "radarr", "film"];
    private static readonly string[] TvCategoryWords = ["tv", "sonarr", "series", "show", "episode"];

    /// <summary>What a library of this media type is called when it is suggested.</summary>
    public static string LibraryName(string mediaScope) => mediaScope == MediaManagerKinds.Tv ? "TV" : "Movies";

    /// <summary>What to tell the person when a download client saves this media type to its one default folder, so no folder is offered.</summary>
    public static string SharedClientFolderNote(string clientLabel, string mediaScope) =>
        $"{clientLabel} has no category folder for {LibraryName(mediaScope)}, so it saves them with every other download and Weir can't tell them apart. " +
        $"Give {LibraryName(mediaScope)} its own category folder in {clientLabel}, then add the workflow under Setup › Workflows.";

    /// <summary>
    /// The folder a download client saves this media type to: the folder of the category named for the type (a
    /// "movies" or "radarr" category, a "tv" or "sonarr" one), or null. A category that names both types, or neither, is
    /// not taken for either, and the client's default completed-downloads folder is never offered: every media type saves
    /// there, so a workflow watching it would overlap every other.
    /// </summary>
    public static string? DownloadClientFolderFor(string mediaScope, DownloadClientFolders folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var own = mediaScope == MediaManagerKinds.Tv ? TvCategoryWords : MovieCategoryWords;
        var other = mediaScope == MediaManagerKinds.Tv ? MovieCategoryWords : TvCategoryWords;
        return folders.CategoryFolders.FirstOrDefault(candidate =>
            DownloadClientPaths.IsAbsolute(candidate.Folder) && NamesType(candidate.Category, own) && !NamesType(candidate.Category, other))?.Folder;
    }

    /// <summary>
    /// Where a library's cleaned files go by default: a "Weir Ready" folder next to the watched folder, with one
    /// folder per media type inside it, so it never sits inside the watched folder. Null when the watched folder has
    /// no parent worth putting files in (a drive root's child on Linux is a container's own disk), so the person
    /// chooses one instead.
    /// </summary>
    public static string? DefaultOutputFolder(string watchedFolder, string mediaScope)
    {
        var watched = new ArrOsPath(watchedFolder.Trim());
        if (!watched.IsRooted)
        {
            return null;
        }

        var segments = watched.Segments;
        var minimumDepth = IsUncPath(watched) ? 3 : 2;
        if (segments.Length < minimumDepth)
        {
            return null;
        }

        var separator = watched.IsWindows ? '\\' : '/';
        var prefix = IsUncPath(watched) ? @"\\" : watched.IsWindows ? string.Empty : "/";
        var parent = segments[..^1];
        return prefix + string.Join(separator, [.. parent, ReadyFolderName, LibraryName(mediaScope)]);
    }

    private static bool IsUncPath(ArrOsPath path) => path.IsWindows && path.Text.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool NamesType(string category, string[] words) =>
        words.Any(word => category.Contains(word, StringComparison.OrdinalIgnoreCase));
}
