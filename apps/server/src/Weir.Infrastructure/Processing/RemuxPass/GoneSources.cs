using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// A file that was deleted, or whose folder was deleted, while Weir had work queued for it. That is an ordinary thing to do to a
/// download, so every route that works on a source file settles it as nothing to do instead of as a failure.
/// </summary>
internal static class GoneSources
{
    /// <summary>How long a file that was not there is given to come back before Weir believes it is gone: a share that drops leaves its mount point behind, empty.</summary>
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(5);

    /// <summary>How long a gone file is held before Weir looks again and, if it is still gone, forgets it: just past the scan's own grace for a vanished file.</summary>
    public static readonly TimeSpan LookAgainAfter = Jobs.VanishedFiles.Grace + TimeSpan.FromMinutes(1);

    /// <summary>
    /// True when the watched folder is there and the file under it is not. A watched folder that is itself missing is a real problem
    /// (an unmounted share, a deleted workflow folder), not a file that went away, so it is never reported as gone.
    /// </summary>
    public static bool HasLeft(string watchedFolder, string relativePath, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        try
        {
            var source = RemuxPassPaths.ResolveMediaFileUnderRoot(watchedFolder, relativePath);
            return !files.FileExists(source) && !files.DirectoryExists(source);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True when the watched folder is there and so is the file under it: neither gone nor unknown.</summary>
    public static bool IsBack(string watchedFolder, string relativePath, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        try
        {
            var source = RemuxPassPaths.ResolveMediaFileUnderRoot(watchedFolder, relativePath);
            return files.FileExists(source) || files.DirectoryExists(source);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
