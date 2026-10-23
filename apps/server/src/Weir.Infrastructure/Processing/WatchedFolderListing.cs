using Weir.Core.Rules;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Processing;

/// <summary>One media file in a watched folder, with the size and times Weir read for it.</summary>
public sealed record WatchedMediaFile(string FullPath, long SizeBytes, DateTime ModifiedUtc, DateTime CreatedUtc)
{
    /// <summary>The modification time as <c>SourceFiles.Fingerprint</c> measures it: nanoseconds since the Unix epoch.</summary>
    public long ModifiedTimeNs => (ModifiedUtc - DateTime.UnixEpoch).Ticks * 100;

    /// <summary>The file as it is on disk now, or null when it is gone or cannot be read.</summary>
    public static WatchedMediaFile? Stat(string path, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        try
        {
            var target = files.GetLinkTarget(path, isDirectory: false) is null ? path : files.ResolveLinkTarget(path, isDirectory: false, returnFinalTarget: true);
            return target is not null && files.FileExists(target)
                ? new WatchedMediaFile(Path.GetFullPath(path), files.GetLength(target), files.GetLastWriteTimeUtc(target), files.GetCreationTimeUtc(target))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A file as a directory listing gave it, or null when it is not there. A link never comes here: it is described by <see cref="Stat"/>.</summary>
    internal static WatchedMediaFile? From(FileInfo info) =>
        info.Exists ? new WatchedMediaFile(info.FullName, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) : null;
}

/// <summary>What a watched-folder walk found, and what it decided not to look at.</summary>
public sealed record WatchedFolderScanCandidates(
    IReadOnlyList<WatchedMediaFile> Entries,
    int IgnoredUnsupportedType,
    IReadOnlyList<string> IgnoredUnsupportedExtensions)
{
    /// <summary>The candidate files' full paths, in walk order.</summary>
    public IReadOnlyList<string> Files => [.. Entries.Select(entry => entry.FullPath)];
}

/// <summary>
/// Walks a watched folder for media candidates: the disk-touching half of the scan rules (#537) that decides which files a
/// scan looks at.
/// </summary>
/// <remarks>
/// The walk reads each file's size and times from the directory listing itself (#708): on a share, a separate stat per file
/// cost several times the walk. A symbolic link is looked up on its own so its target's size is the one recorded.
/// </remarks>
public static class WatchedFolderListing
{
    /// <summary>Files that legitimately sit beside media and are not a failed attempt at it.</summary>
    private static readonly HashSet<string> NonMediaCompanionSuffixes = new(StringComparer.Ordinal)
    {
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".sup", ".nfo", ".txt", ".md", ".log", ".xml", ".json",
        ".yml", ".yaml", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".tbn", ".bmp", ".par2", ".sfv", ".nzb",
        ".torrent", ".url", ".db", ".ini", ".bak", ".mp3", ".flac", ".m4a", ".aac", ".ac3", ".dts", ".ogg", ".wav",
        ".part", ".partial", ".crdownload", ".downloading", ".tmp", ".!ut", ".!qb",
    };

    private static readonly HashSet<string> DefaultTransientDownloadDirMarkers = new(StringComparer.Ordinal)
    {
        ".sabnzbd", "__admin__", "_failed_", "_unpack_", "_repair_", "incomplete",
    };

    /// <summary>Whether the path is an existing file with a media extension (the allowlist itself is
    /// <see cref="RemuxRules.MediaExtensions"/>).</summary>
    public static bool IsProcessingMediaCandidate(string path) => IsProcessingMediaCandidate(path, PhysicalFileSystem.Instance);

    /// <inheritdoc cref="IsProcessingMediaCandidate(string)"/>
    public static bool IsProcessingMediaCandidate(string path, IFileSystem files)
    {
        return files.FileExists(path) && IsMediaExtension(Path.GetExtension(path).ToLowerInvariant());
    }

    /// <summary>Whether the path looks like a download client's in-progress artifact: a hash-named file, or a
    /// file under a folder named by one of the exclude markers.</summary>
    public static bool IsTransientDownloadArtifactMediaPath(string path, IReadOnlyCollection<string>? excludeMarkers)
    {
        var stem = Path.GetFileNameWithoutExtension(path).Trim();
        if (stem.Length is >= 32 and <= 64 && stem.All(Uri.IsHexDigit))
        {
            return true;
        }

        return HasDownloaderFolder(path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries), excludeMarkers);
    }

    /// <summary>
    /// Whether any of the folder names is one a downloader uses while a download is unfinished: the workflow's own list of
    /// folder names, or the built-in list when it names none. Names are compared without regard to case.
    /// </summary>
    internal static bool HasDownloaderFolder(IEnumerable<string> folderNames, IReadOnlyCollection<string>? excludeMarkers)
    {
        var markers = excludeMarkers is { Count: > 0 }
            ? excludeMarkers.Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0).ToHashSet(StringComparer.Ordinal)
            : DefaultTransientDownloadDirMarkers;
        return folderNames.Select(name => name.Trim().ToLowerInvariant()).Any(markers.Contains);
    }

    /// <summary>Candidate files under <paramref name="watchedRoot"/>, ordered by path,
    /// plus a count of what the allowlist rejected. Files only; directories are never returned.</summary>
    public static WatchedFolderScanCandidates Candidates(
        string watchedRoot,
        IReadOnlyCollection<string>? mediaExtensions,
        IReadOnlyCollection<string>? excludeMarkers,
        bool excludeHidden,
        bool topLevelOnly,
        IFileSystem? files = null)
    {
        var root = Path.GetFullPath(watchedRoot);
        var found = new List<WatchedMediaFile>();
        var rejected = 0;
        var rejectedSuffixes = new SortedSet<string>(StringComparer.Ordinal);
        var configuredExtensions = ConfiguredExtensions(mediaExtensions);
        foreach (var info in Walk(root, topLevelOnly, files: files).OrderBy(file => file.FullName, StringComparer.Ordinal))
        {
            var p = info.FullName;
            var relative = Path.GetRelativePath(root, p);
            var relativeParts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if ((excludeHidden && relativeParts.Any(part => part.StartsWith('.'))) || IsTransientDownloadArtifactMediaPath(p, excludeMarkers))
            {
                continue;
            }

            var suffix = Path.GetExtension(p).ToLowerInvariant();
            if (!(configuredExtensions?.Contains(suffix) ?? IsMediaExtension(suffix)))
            {
                if (suffix.Length > 0 && !NonMediaCompanionSuffixes.Contains(suffix))
                {
                    rejected++;
                    rejectedSuffixes.Add(suffix);
                }

                continue;
            }

            if ((info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? WatchedMediaFile.Stat(p, files) : WatchedMediaFile.From(info)) is { } file)
            {
                found.Add(file);
            }
        }

        return new WatchedFolderScanCandidates(found, rejected, [.. rejectedSuffixes]);
    }

    /// <summary>
    /// Every file under the root, followed through symbolic links the way a path lookup follows them. One folder at a time, so a
    /// folder deleted while the walk is under way costs only itself: its files are gone, and the rest of the tree is still listed.
    /// A folder Weir may not read still fails the scan, which is a real problem. <paramref name="list"/> is the test seam that
    /// reads one folder.
    /// </summary>
    internal static List<FileInfo> Walk(string root, bool topLevelOnly, Func<DirectoryInfo, IEnumerable<FileSystemInfo>>? list = null, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        list ??= folder => files.EnumerateFileSystemInfos(folder.FullName);
        var found = new List<FileInfo>();
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (pending.TryPop(out var folder))
        {
            try
            {
                foreach (var entry in list(folder))
                {
                    if ((entry as FileInfo ?? LinkedFile(entry, files)) is { } file)
                    {
                        found.Add(file);
                    }
                    else if (entry is DirectoryInfo subfolder && !topLevelOnly)
                    {
                        pending.Push(subfolder);
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // This folder was deleted under the walk.
            }
        }

        return found;
    }

    /// <summary>A directory entry that is a symbolic link to a file counts as that file, as <see cref="File.Exists"/> treats it.</summary>
    private static FileInfo? LinkedFile(FileSystemInfo entry, IFileSystem files) =>
        entry.LinkTarget is not null && files.FileExists(entry.FullName) ? new FileInfo(entry.FullName) : null;

    /// <summary>The extensions a workflow names, lower-case with a leading dot, or null when it names none and the built-in list applies.</summary>
    internal static HashSet<string>? ConfiguredExtensions(IReadOnlyCollection<string>? mediaExtensions) =>
        mediaExtensions is { Count: > 0 }
            ? mediaExtensions
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .Select(v => v.StartsWith('.') ? v.ToLowerInvariant() : "." + v.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal)
            : null;

    internal static bool IsMediaExtension(string suffix) => RemuxRules.MediaExtensions.Contains(suffix);
}
