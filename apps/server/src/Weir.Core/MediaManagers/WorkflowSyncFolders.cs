using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// The folder a Deluno library's downloads arrive in as Weir sees it, or null with the plain sentence that says why
/// Deluno does not give one.
/// </summary>
public sealed record SyncedWatchedFolder(string? Folder, string? Problem);

/// <summary>
/// Which folders a workflow set up from Deluno watches and writes to. Deluno's library <c>downloadsPath</c> is often empty
/// ("use the download client's reported folder" is Deluno's default), so the folders its download clients really save to
/// (<see cref="DelunoDestination.SaveFolder"/>) come first and <c>downloadsPath</c> is only the fallback. Every folder is
/// turned into Weir's view through Deluno's path mappings for Weir (<see cref="DelunoPathMappings"/>).
/// </summary>
public static class WorkflowSyncFolders
{
    /// <summary>
    /// The folder to watch. Clients that save to one folder give that folder. Clients that save to several give the folder
    /// that holds them all, when there is one that is not a drive or filesystem root; otherwise the first, and the Folder
    /// chain reports the rest. With no client folder, the library's downloads folder; with neither, no folder and the
    /// sentence that tells the person where to set one.
    /// </summary>
    /// <param name="managerLabel">How the manager is named to people, e.g. "Deluno".</param>
    /// <param name="libraryName">The library, as the manager names it.</param>
    /// <param name="downloadsPath">The manifest's <c>downloadsPath</c>, in the manager's view.</param>
    /// <param name="destinations">Where the library's clients save and the path mappings, or null when the manager did not publish them.</param>
    public static SyncedWatchedFolder Watched(
        string managerLabel, string libraryName, string? downloadsPath, DelunoLibraryDestinations? destinations)
    {
        var mappings = destinations?.PathMappings ?? [];
        var saved = (destinations?.Destinations ?? [])
            .Select(destination => Mapped(mappings, destination.SaveFolder))
            .OfType<string>()
            .ToList();
        if (saved.Count > 0)
        {
            return new SyncedWatchedFolder(Unify(saved), null);
        }

        var downloads = Mapped(mappings, downloadsPath ?? destinations?.DownloadsPath);
        return downloads is null
            ? new SyncedWatchedFolder(null, NoDownloadsFolder(managerLabel, libraryName))
            : new SyncedWatchedFolder(downloads, null);
    }

    /// <summary>The folder the manager picks cleaned files up from, in Weir's view; null when it names none.</summary>
    public static string? Output(string? processorOutputPath, DelunoLibraryDestinations? destinations) =>
        Mapped(destinations?.PathMappings ?? [], processorOutputPath);

    /// <summary>What to tell the person when the manager gives no folder for a library's downloads.</summary>
    public static string NoDownloadsFolder(string managerLabel, string libraryName) =>
        $"{managerLabel} doesn't say where downloads for {libraryName} arrive. " +
        $"Set the downloads folder in {managerLabel} (or the clients' category folders) and Weir will pick it up.";

    /// <summary>What to tell the person when the folder the manager gives for a library's downloads is also another library's, so it is no one's own.</summary>
    public static string SharedDownloadsFolder(string managerLabel, string libraryName, string folder) =>
        $"{managerLabel} gives {folder} for {WorkflowSyncRules.Possessive(libraryName)} downloads, but another library's downloads arrive there too, so it is not {WorkflowSyncRules.Possessive(libraryName)} own folder. " +
        $"Give each library its own downloads folder in {managerLabel} (or its own category folder in the clients) and Weir will pick it up.";

    /// <summary>What to tell the person when the manager gives no folder to pick cleaned files up from.</summary>
    public static string NoOutputFolder(string managerLabel, string libraryName) =>
        $"{managerLabel} doesn't say where it picks up processed files for {libraryName}. " +
        $"Set the processed folder for that library in {managerLabel} and Weir will pick it up.";

    /// <summary>
    /// The one folder that stands for <paramref name="folders"/>: the folder itself when they are all the same, their
    /// deepest common parent when that is a real folder (not a drive or filesystem root), else the first.
    /// </summary>
    private static string Unify(IReadOnlyList<string> folders)
    {
        var distinct = new List<string>();
        foreach (var folder in folders)
        {
            if (!IsAmong(distinct, folder))
            {
                distinct.Add(folder);
            }
        }

        return distinct.Count == 1 ? distinct[0] : CommonParent(distinct) ?? distinct[0];
    }

    private static bool IsAmong(List<string> folders, string folder)
    {
        var wanted = new ArrOsPath(folder);
        foreach (var seen in folders)
        {
            if (new ArrOsPath(seen).SameFolder(wanted))
            {
                return true;
            }
        }

        return false;
    }

    private static string? CommonParent(IReadOnlyList<string> folders)
    {
        var paths = folders.Select(folder => new ArrOsPath(folder)).ToList();
        if (paths.Any(path => !path.IsRooted || path.IsWindows != paths[0].IsWindows))
        {
            return null;
        }

        var first = paths[0].Segments;
        var shared = first.Length;
        var comparison = paths[0].IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var other in paths.Skip(1).Select(path => path.Segments))
        {
            var length = 0;
            while (length < shared && length < other.Length && string.Equals(first[length], other[length], comparison))
            {
                length++;
            }

            shared = length;
        }

        var unc = paths[0].IsWindows && paths[0].Text.StartsWith(@"\\", StringComparison.Ordinal);
        var minimumDepth = unc ? 3 : paths[0].IsWindows ? 2 : 1;
        if (shared < minimumDepth)
        {
            return null;
        }

        var parent = first[..shared];
        return !paths[0].IsWindows ? "/" + string.Join('/', parent) : (unc ? @"\\" : string.Empty) + string.Join('\\', parent);
    }

    private static string? Mapped(IReadOnlyList<DelunoPathMapping> mappings, string? folder)
    {
        var text = WireStrings.Strip(folder ?? string.Empty);
        return text.Length == 0 ? null : DelunoPathMappings.Apply(mappings, text).Path;
    }
}
