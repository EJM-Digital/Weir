using Weir.Core.LibraryMode;
using Weir.Core.Processing;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>One media file a library-folder walk found, before it is probed.</summary>
public sealed record LibraryWalkedFile(string Path, long SizeBytes, long ModifiedTimeUnixSeconds);

/// <summary>
/// Walks a library's #505 library folders for media files, read-only. Applies the library's file types, downloader folders,
/// hidden-file and top-folder settings the way the watched-folder scan does, so "which files count" agrees with the rest of the
/// library's settings. Folder names are judged below the library folder, never on the folder's own path.
/// </summary>
public static class LibraryFileWalker
{
    public static IReadOnlyList<LibraryWalkedFile> Walk(ProcessingLibraryRecord library, LibrarySettings settings, IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        files ??= PhysicalFileSystem.Instance;
        ArgumentNullException.ThrowIfNull(settings);
        var folders = settings.Folders;
        var extensions = WatchedFolderListing.ConfiguredExtensions(SplitCsv(library.MediaExtensionsCsv));
        var excludeMarkers = SplitCsv(library.ExcludeMarkersCsv);
        // #735: a kept original sits inside the library folder it came from by default, but it must never be read back
        // in as a library file of its own — each library folder gets its own originals folder excluded here.
        var excludedFolders = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => OriginalsPathPlanner.DestinationFolder(folder, settings.OriginalsFolder))
            .ToList();
        var found = new List<LibraryWalkedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !files.DirectoryExists(folder))
            {
                continue;
            }

            var option = new EnumerationOptions { RecurseSubdirectories = !library.TopLevelOnly, AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Win32 };
            foreach (var path in files.EnumerateFiles(folder, "*", option))
            {
                if (!seen.Add(path))
                {
                    continue;
                }

                var name = Path.GetFileName(path);
                var folderNames = Path.GetRelativePath(folder, Path.GetDirectoryName(path)!).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Where(part => part.Length > 0 && part != ".");
                if (library.ExcludeHidden && (name.StartsWith('.') || folderNames.Any(part => part.StartsWith('.'))))
                {
                    continue;
                }

                if (excludedFolders.Any(excluded => OriginalsPathPlanner.IsWithin(excluded, path)))
                {
                    continue;
                }

                var extension = Path.GetExtension(name).ToLowerInvariant();
                if (!(extensions?.Contains(extension) ?? WatchedFolderListing.IsMediaExtension(extension)))
                {
                    continue;
                }

                if (WatchedFolderListing.HasDownloaderFolder(folderNames, excludeMarkers))
                {
                    continue;
                }

                if (WatchedMediaFile.Stat(path, files) is { } file)
                {
                    found.Add(new LibraryWalkedFile(path, file.SizeBytes, new DateTimeOffset(file.ModifiedUtc, TimeSpan.Zero).ToUnixTimeSeconds()));
                }
            }
        }

        return found;
    }

    private static List<string> SplitCsv(string? csv) =>
        (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
