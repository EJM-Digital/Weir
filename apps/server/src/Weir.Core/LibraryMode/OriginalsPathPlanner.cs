namespace Weir.Core.LibraryMode;

/// <summary>
/// #735's "keep the original after a library clean": where a kept original goes. Pure path arithmetic only — the
/// actual move (or cross-volume copy) is <c>Weir.Infrastructure.LibraryMode.OriginalsMover</c>.
/// </summary>
/// <remarks>
/// Deliberately never uses <see cref="Path.Combine(string, string)"/> or <see cref="Path.GetRelativePath(string, string)"/>:
/// both assume the running OS's own separator, but a library folder can be typed with either style (a Docker container
/// reading a Windows share, for instance). Every join below reuses whichever separator the input path already has, the same
/// way <see cref="SafeSwapRules"/> builds its temp and backup names.
/// </remarks>
public static class OriginalsPathPlanner
{
    /// <summary>
    /// The default originals folder's name, inside whichever library folder held the file, used when a library has
    /// not set an explicit <see cref="LibrarySettings.OriginalsFolder"/>. Dot-prefixed so it reads as Weir's own
    /// folder, not part of the media collection.
    /// </summary>
    public const string DefaultFolderName = ".weir-originals";

    /// <summary>The library folder (one of <paramref name="libraryFolders"/>) that contains <paramref name="filePath"/>, or
    /// null when none of them do.</summary>
    public static string? ContainingFolder(IReadOnlyList<string> libraryFolders, string filePath)
    {
        ArgumentNullException.ThrowIfNull(libraryFolders);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        foreach (var folder in libraryFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder) && IsWithin(folder, filePath))
            {
                return folder;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="folder"/> itself, or something inside it, once both are settled as text: <c>.</c> and
    /// <c>..</c> segments are resolved, so a path that climbs out of the folder is not inside it however it is spelled.
    /// </summary>
    public static bool IsWithin(string folder, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Within(Normalised(folder), Normalised(path));
    }

    /// <summary>
    /// Where kept originals for files under <paramref name="libraryFolder"/> go: <paramref name="originalsFolder"/> when the
    /// library has set one, else the <see cref="DefaultFolderName"/> folder inside <paramref name="libraryFolder"/> itself.
    /// </summary>
    public static string DestinationFolder(string libraryFolder, string? originalsFolder)
    {
        if (!string.IsNullOrWhiteSpace(originalsFolder))
        {
            return originalsFolder;
        }

        ArgumentException.ThrowIfNullOrEmpty(libraryFolder);
        return $"{TrimTrailingSeparator(libraryFolder)}{SeparatorOf(libraryFolder)}{DefaultFolderName}";
    }

    /// <summary>
    /// Where a kept original of <paramref name="filePath"/> (a file under <paramref name="libraryFolder"/>) belongs, before
    /// any clash with a file already there is resolved: <see cref="DestinationFolder"/> plus the file's path relative to
    /// <paramref name="libraryFolder"/>, so files from different sub-folders never collide on name alone.
    /// </summary>
    public static string DestinationPath(string libraryFolder, string? originalsFolder, string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var folder = DestinationFolder(libraryFolder, originalsFolder);
        var relative = RelativePath(libraryFolder, filePath);
        return $"{TrimTrailingSeparator(folder)}{SeparatorOf(folder)}{relative}";
    }

    /// <summary>
    /// The <paramref name="attempt"/>-th candidate name for a kept original at <paramref name="path"/>: <paramref name="path"/>
    /// itself for attempt 1, then a numbered suffix from attempt 2 on (<c>Movie.mkv</c> becomes <c>Movie (2).mkv</c>, then
    /// <c>(3)</c>). Pure: whether a candidate is actually free is for the caller to find out, atomically, when it tries to
    /// claim it — a snapshot check run just before this one cannot see another swap reserving the same name at the same
    /// moment. See <c>Weir.Infrastructure.LibraryMode.OriginalsMover.Reserve</c>.
    /// </summary>
    public static string CandidateAt(string path, int attempt)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (attempt == 1)
        {
            return path;
        }

        var lastSeparator = path.LastIndexOfAny(['\\', '/']);
        var directory = lastSeparator < 0 ? string.Empty : path[..(lastSeparator + 1)];
        var name = path[(lastSeparator + 1)..];
        var dot = name.LastIndexOf('.');
        var stem = dot <= 0 ? name : name[..dot];
        var extension = dot <= 0 ? string.Empty : name[dot..];
        return $"{directory}{stem} ({attempt}){extension}";
    }

    /// <summary>
    /// <paramref name="path"/> below <paramref name="folder"/>, settled as text, in the separator the folder uses. Falls back to just the file
    /// name when <paramref name="path"/> is not actually under <paramref name="folder"/> (a defensive case: callers normally resolve
    /// <paramref name="folder"/> from <see cref="ContainingFolder"/> first).
    /// </summary>
    private static string RelativePath(string folder, string path)
    {
        var inside = Normalised(folder);
        var target = Normalised(path);
        if (Within(inside, target) && target.Parts!.Count > inside.Parts!.Count)
        {
            return string.Join(SeparatorOf(folder), target.Parts.Skip(inside.Parts.Count));
        }

        // The file's own name; a path that ends in '.' or '..' has no name to keep, and must not become one.
        return path.Split('\\', '/').LastOrDefault(part => part.Length > 0 && part is not ("." or "..")) ?? "original";
    }

    /// <summary>A path as the parts of its root and its names, with <c>.</c> and <c>..</c> resolved; the parts are null when it climbs above its root.</summary>
    private readonly record struct Settled(string Root, List<string>? Parts);

    private static Settled Normalised(string path)
    {
        var text = path;
        var root = text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("\\\\", StringComparison.Ordinal)
            ? "//"
            : text.StartsWith('/') || text.StartsWith('\\') ? "/" : string.Empty;
        var parts = new List<string>();
        foreach (var part in text.Split('\\', '/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part != "..")
            {
                parts.Add(part);
            }
            else if (parts.Count == 0)
            {
                return new Settled(root, null);
            }
            else
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        return new Settled(root, parts);
    }

    private static bool Within(Settled folder, Settled path) =>
        folder.Parts is { } inside && path.Parts is { } target && folder.Root == path.Root && target.Count >= inside.Count &&
        inside.SequenceEqual(target.Take(inside.Count), StringComparer.OrdinalIgnoreCase);

    private static string TrimTrailingSeparator(string path) => path.TrimEnd('\\', '/');

    /// <summary>The separator a path already uses, so a join never mixes styles: backslash only when the path has one and
    /// no forward slash at all, forward slash otherwise (POSIX default).</summary>
    private static char SeparatorOf(string path) => path.Contains('\\', StringComparison.Ordinal) && !path.Contains('/', StringComparison.Ordinal) ? '\\' : '/';
}
