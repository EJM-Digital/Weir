using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Where a path really leads once every junction and symbolic link on the way is followed, so two names for one folder
/// (a mount reached as <c>C:\NasMount</c> and as <c>C:\Downloads</c>, say) compare as the one folder they are.
/// </summary>
public static class FinalPaths
{
    /// <summary>A loop of links, or an absurdly deep chain, gives up rather than spinning.</summary>
    private const int MaxLinksFollowed = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// The path with every link followed, or null when that cannot be told from this machine: the path is not fully
    /// qualified, does not exist here, or a link on the way is broken or unreadable. It reads only link targets and never
    /// creates or changes anything.
    /// </summary>
    public static string? Resolve(string path, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var resolved = Path.GetPathRoot(full)!;
            var pending = new Stack<string>(PartsBelowRoot(full).Reverse());
            var linksFollowed = 0;
            while (pending.Count > 0)
            {
                var candidate = Path.Join(resolved, pending.Pop());
                var isDirectory = files.DirectoryExists(candidate);
                if (!isDirectory && !files.FileExists(candidate))
                {
                    return null;
                }

                if (files.GetLinkTarget(candidate, isDirectory) is null)
                {
                    resolved = candidate;
                    continue;
                }

                if (++linksFollowed > MaxLinksFollowed || files.ResolveLinkTarget(candidate, isDirectory, returnFinalTarget: false) is not { } target)
                {
                    return null;
                }

                resolved = Path.GetPathRoot(target)!;
                foreach (var part in PartsBelowRoot(target).Reverse())
                {
                    pending.Push(part);
                }
            }

            return resolved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string[] PartsBelowRoot(string fullPath) =>
        fullPath[Path.GetPathRoot(fullPath)!.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
}
