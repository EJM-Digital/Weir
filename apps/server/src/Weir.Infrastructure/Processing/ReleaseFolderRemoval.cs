using System.Text.RegularExpressions;
using Weir.Core.Rules;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Processing;

/// <summary>What removing a finished or failed movie's source did.</summary>
public enum ReleaseRemovalKind
{
    /// <summary>The whole release folder was removed.</summary>
    FolderRemoved,

    /// <summary>The folder holds other videos, so only this file was removed.</summary>
    FileOnly,

    /// <summary>Nothing was removed; <see cref="ReleaseRemoval.Reason"/> says why.</summary>
    NothingRemoved,
}

/// <summary>The outcome of <see cref="ReleaseFolderRemoval.Remove"/>, with the sentence an operator reads when the folder stayed.</summary>
public sealed record ReleaseRemoval(ReleaseRemovalKind Kind, string? Reason)
{
    public bool FolderRemoved => Kind == ReleaseRemovalKind.FolderRemoved;

    public bool FileRemoved => Kind is ReleaseRemovalKind.FolderRemoved or ReleaseRemovalKind.FileOnly;
}

/// <summary>
/// Removes a movie's source release: the folder the file sits in, but only when nothing else in it is a video. A
/// collection pack, or a category folder such as <c>movies\Film.mkv</c>, holds other films that are not this file's
/// to delete, so there only the file itself goes. One rule for every lane that removes a movie's source (the pass
/// after success, the scan's retry of an interrupted removal, failure cleanup); each lane calls it on its own.
/// </summary>
public static partial class ReleaseFolderRemoval
{
    public const string OtherVideosReason = "The folder also holds other videos, so Weir removed only this file.";

    public const string OtherVideosFolderKeptReason = "The folder also holds other videos, so Weir left it in place.";

    public const string LinkedFolderReason =
        "The release folder, or a folder above it inside the watched folder, is a link to another place, so Weir did not remove it.";

    public const string UnreadableFolderReason = "Weir could not look through the whole release folder, so it removed only this file.";

    /// <summary>A sample clip is smaller than this; anything larger is treated as a film whatever it is called.</summary>
    public const long SampleMaxBytes = 300L * 1024 * 1024;

    /// <summary>
    /// Removes <paramref name="file"/>'s folder when it holds no other video, otherwise only <paramref name="file"/>.
    /// Nothing is removed when the folder is not strictly inside <paramref name="root"/> or sits behind a link. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when a delete fails.
    /// </summary>
    /// <param name="root">The library root the folder lies in (the watched or output folder).</param>
    /// <param name="file">The movie file whose release this is. It need not exist.</param>
    /// <param name="mediaExtensionsCsv">The library's own media extensions, added to the built-in video containers.</param>
    /// <param name="files">The file system to use; the real one when omitted.</param>
    public static ReleaseRemoval Remove(string root, string file, string? mediaExtensionsCsv, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(file);
        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        if (folder is null || !PathContainment.IsUnder(root, folder))
        {
            return new ReleaseRemoval(ReleaseRemovalKind.NothingRemoved, "The release folder is not inside the workflow's folder, so Weir did not change it.");
        }

        if (PathContainment.HasLinkBelowRoot(root, folder, files))
        {
            return new ReleaseRemoval(ReleaseRemovalKind.NothingRemoved, LinkedFolderReason);
        }

        bool? othersPresent;
        try
        {
            othersPresent = HoldsOtherVideos(folder, file, VideoExtensions(mediaExtensionsCsv), files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            othersPresent = null;
        }

        if (othersPresent != false)
        {
            if (files.FileExists(file))
            {
                DeleteIfStillThere(() => files.DeleteFile(file));
            }

            return new ReleaseRemoval(ReleaseRemovalKind.FileOnly, othersPresent is null ? UnreadableFolderReason : OtherVideosReason);
        }

        if (files.DirectoryExists(folder))
        {
            DeleteIfStillThere(() => files.DeleteDirectory(folder, recursive: true));
        }

        return new ReleaseRemoval(ReleaseRemovalKind.FolderRemoved, null);
    }

    /// <summary>Something that removes the file or folder first has done what was asked: only a failure that leaves it in place is an error.</summary>
    private static void DeleteIfStillThere(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// Whether anything under <paramref name="folder"/>, other than <paramref name="file"/>, is a video with one of
    /// <paramref name="extensions"/>. Samples do not count, so a normal release folder with its sample and .nfo still
    /// goes as a whole. Links inside the folder are not followed. Throws when part of the folder cannot be read.
    /// </summary>
    public static bool HoldsOtherVideos(string folder, string file, IReadOnlySet<string> extensions, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(extensions);
        if (!files.DirectoryExists(folder))
        {
            return false;
        }

        var self = Path.GetFullPath(file);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };
        foreach (var candidate in files.EnumerateFiles(folder, "*", options))
        {
            if (string.Equals(Path.GetFullPath(candidate), self, comparison) ||
                !extensions.Contains(Path.GetExtension(candidate).ToLowerInvariant()))
            {
                continue;
            }

            // A clip named or filed as a sample, and small enough to be one; a full film that happens to have "sample" in
            // its title still counts.
            if (!IsSample(Path.GetRelativePath(folder, candidate)) || files.GetLength(candidate) >= SampleMaxBytes)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A sample clip: a <c>sample</c> or <c>samples</c> folder on the way, or "sample" as a word in the name.</summary>
    public static bool IsSample(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        return parts[..^1].Any(part => part.Equals("sample", StringComparison.OrdinalIgnoreCase) || part.Equals("samples", StringComparison.OrdinalIgnoreCase)) ||
               SampleWord().IsMatch(Path.GetFileNameWithoutExtension(parts[^1]));
    }

    /// <summary>The built-in video containers plus the library's own list.</summary>
    public static IReadOnlySet<string> VideoExtensions(string? mediaExtensionsCsv)
    {
        var set = new HashSet<string>(RemuxRules.MediaExtensions, StringComparer.Ordinal);
        foreach (var raw in (mediaExtensionsCsv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(raw.StartsWith('.') ? raw.ToLowerInvariant() : "." + raw.ToLowerInvariant());
        }

        return set;
    }

    [GeneratedRegex("(^|[^a-z])sample([^a-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SampleWord();
}
