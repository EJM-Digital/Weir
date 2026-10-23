namespace Weir.Infrastructure.IO;

/// <summary>
/// Whether one path lies inside a folder, for the checks that guard a delete. A plain prefix test is not enough:
/// <c>/data/movies-old</c> starts with <c>/data/movies</c> but is not inside it, and Linux paths are case-sensitive.
/// </summary>
public static class PathContainment
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// True when <paramref name="path"/> is strictly inside <paramref name="root"/>. Both are made absolute first, so
    /// <c>..</c> segments cannot climb out, and the match must end on a separator. The root itself is not inside itself.
    /// Case is ignored on Windows only. Throws what <see cref="Path.GetFullPath(string)"/> throws for an invalid path.
    /// </summary>
    public static bool IsUnder(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(fullPath, fullRoot, Comparison))
        {
            return false;
        }

        // A drive or filesystem root ("C:\", "/") keeps its separator after trimming.
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// True when <paramref name="path"/>, or any folder between it and <paramref name="root"/> (the root itself not
    /// included), is a junction or symbolic link. A recursive delete through a link would remove files that live
    /// somewhere else, so callers refuse it. A path that cannot be inspected counts as a link.
    /// </summary>
    public static bool HasLinkBelowRoot(string root, string path, IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        files ??= PhysicalFileSystem.Instance;
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        while (IsUnder(root, current))
        {
            try
            {
                if ((files.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Nothing there to follow; the folders above it still decide.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }

            // Strictly under the root, so it is not a filesystem root itself and has a parent.
            current = Path.GetDirectoryName(current)!;
        }

        return false;
    }
}
