using Weir.Core.Media;

namespace Weir.Core.Processing;

/// <summary>
/// Pure validation for a Processing library's watched/work/output folders: reserved-path checks and the
/// pairwise/cross-library overlap check. Split out of <see cref="LibraryRules"/>, which owns the rest of a
/// library's field validation.
/// </summary>
public static partial class LibraryRules
{
    /// <summary>A library's folder path, normalized for the overlap check.</summary>
    public static string? NormalizeFolder(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var slashed = text.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        return slashed.Length == 0 ? "/" : slashed;
    }

    /// <summary>Equal, or one a path-segment ancestor of the other.</summary>
    public static bool FoldersOverlap(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        return IsAncestor(a, b) || IsAncestor(b, a);
    }

    /// <summary>Whether <paramref name="ancestor"/> is a path-segment ancestor of <paramref name="descendant"/> (not equal to
    /// it). Directional, unlike <see cref="FoldersOverlap"/>: <c>Weir.Core.LibraryMode.LibraryFolderRules</c> reuses this to
    /// tell "originals folder sits inside a library folder" (fine) from "originals folder contains one" (not fine).</summary>
    internal static bool IsAncestor(string ancestor, string descendant) =>
        descendant.Length > ancestor.Length &&
        descendant.StartsWith(ancestor, StringComparison.Ordinal) &&
        (ancestor == "/" || descendant[ancestor.Length] == '/');

    /// <summary>The Linux directories a watched, work, output or library folder may never be, or be inside.</summary>
    private static readonly string[] LinuxReservedRoots =
        ["/etc", "/usr", "/bin", "/sbin", "/boot", "/proc", "/sys", "/dev", "/var/lib"];

    /// <summary>
    /// A folder a library create/update/restore is about to save: must be absolute, may not contain a
    /// <c>..</c> segment, and may not be a drive or filesystem root, Weir's own data folder, a Windows
    /// system folder, or one of a fixed list of Linux system folders. Applied only to the folder being
    /// saved right now, so a library already pointing at such a folder from before this rule existed
    /// keeps working until it is next saved.
    /// </summary>
    /// <param name="label">What the folder is for, used in the exception message ("watched", "output", ...).</param>
    /// <param name="raw">The folder path as given; empty or whitespace is a no-op.</param>
    /// <param name="weirHome">Weir's own data folder; null skips that one check.</param>
    public static void ValidateFolderPath(string label, string? raw, string? weirHome = null)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (HasParentSegment(text))
        {
            throw new ProcessingLibraryException($"The {label} folder can't contain a '..' segment. Enter the folder's full path instead.");
        }

        if (!LooksAbsolute(text))
        {
            throw new ProcessingLibraryException($"The {label} folder must be an absolute path.");
        }

        var windows = LooksLikeWindowsPath(text);
        var full = MediaToolLocations.Normalize(text, windows);
        RejectReservedFolder(label, full, windows, weirHome);
    }

    /// <summary>A path segment equal to <c>..</c>, under either separator style.</summary>
    private static bool HasParentSegment(string text) =>
        text.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "..");

    /// <summary>A Unix root, a Windows drive-letter path, or a rooted/UNC path starting with a backslash.</summary>
    private static bool LooksAbsolute(string text) =>
        text.StartsWith('/') || text.StartsWith('\\') ||
        (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'));

    /// <summary>A drive-letter or backslash-rooted path is Windows-form; everything else is Unix-form.</summary>
    private static bool LooksLikeWindowsPath(string text) =>
        text.StartsWith('\\') || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':');

    private static void RejectReservedFolder(string label, string fullPath, bool windows, string? weirHome)
    {
        if (IsDriveOrFilesystemRoot(fullPath, windows))
        {
            throw new ProcessingLibraryException($"Weir can't use the root of a drive as a {label} folder. Choose a folder inside it.");
        }

        if (!string.IsNullOrWhiteSpace(weirHome) && IsSameOrInside(fullPath, MediaToolLocations.Normalize(weirHome.Trim(), windows), windows))
        {
            throw new ProcessingLibraryException($"Weir can't use its own data folder as a {label} folder. Choose a different folder.");
        }

        if (windows)
        {
            foreach (var special in WindowsReservedRoots())
            {
                if (IsSameOrInside(fullPath, special, windows))
                {
                    throw new ProcessingLibraryException($"Weir can't use a Windows system folder as a {label} folder. Choose a folder outside it.");
                }
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (PathsEqual(fullPath, MediaToolLocations.Normalize(userProfile, windows), windows))
            {
                throw new ProcessingLibraryException($"Weir can't use your whole user profile folder as a {label} folder. Choose a folder inside it.");
            }
        }
        else
        {
            foreach (var reserved in LinuxReservedRoots)
            {
                if (IsSameOrInside(fullPath, reserved, windows))
                {
                    throw new ProcessingLibraryException($"Weir can't use a system folder as a {label} folder. Choose a folder outside it.");
                }
            }
        }
    }

    private static bool IsDriveOrFilesystemRoot(string fullPath, bool windows)
    {
        if (!windows)
        {
            return fullPath == "/";
        }

        var trimmed = fullPath.TrimEnd('\\');
        return trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':';
    }

    private static bool PathsEqual(string a, string b, bool windows) =>
        string.Equals(a, b, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Whether <paramref name="fullPath"/> is <paramref name="reserved"/> itself or a folder inside it.</summary>
    private static bool IsSameOrInside(string fullPath, string reserved, bool windows)
    {
        var trimmedReserved = reserved.TrimEnd('\\', '/');
        if (PathsEqual(fullPath, trimmedReserved, windows))
        {
            return true;
        }

        var withSeparator = trimmedReserved + (windows ? '\\' : '/');
        return fullPath.StartsWith(withSeparator, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>The Windows directory and both Program Files folders, normalized; empty (non-Windows host) entries are skipped.</summary>
    private static IEnumerable<string> WindowsReservedRoots() => WindowsReservedRoots(Environment.GetFolderPath);

    /// <summary><see cref="WindowsReservedRoots()"/> with the system's folders passed in, so a host that has them and one that has not are both exercised on any host.</summary>
    internal static IEnumerable<string> WindowsReservedRoots(Func<Environment.SpecialFolder, string> folderPath)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = folderPath(folder);
            if (!string.IsNullOrEmpty(path))
            {
                yield return MediaToolLocations.Normalize(path, windows: true);
            }
        }
    }

    /// <summary>
    /// A library's own three folders must be distinct, and its watched/output
    /// folders must not overlap any other library's watched/output folders.
    /// </summary>
    /// <param name="watchedFolder">The library's watched folder, or empty/null when unset.</param>
    /// <param name="workFolder">The library's work folder, or empty/null when unset.</param>
    /// <param name="outputFolder">The library's output folder, or empty/null when unset.</param>
    /// <param name="others">Every other library's watched/output folders, for the overlap check.</param>
    /// <param name="weirHome">Weir's own data folder, refused as a library folder (see
    /// <see cref="ValidateFolderPath"/>); null skips that one check, for callers that don't have it.</param>
    public static void ValidateFolders(
        string? watchedFolder,
        string? workFolder,
        string? outputFolder,
        IReadOnlyList<OtherLibraryFolders> others,
        string? weirHome = null)
    {
        ValidateFolderPath("watched", watchedFolder, weirHome);
        ValidateFolderPath("work", workFolder, weirHome);
        ValidateFolderPath("output", outputFolder, weirHome);

        var watched = NormalizeFolder(watchedFolder);
        var work = NormalizeFolder(workFolder);
        var output = NormalizeFolder(outputFolder);

        if (watched is not null && output is null)
        {
            throw new ProcessingLibraryException("Set an output folder as well as a watched folder, so processed files have somewhere to go.");
        }

        foreach (var (labelA, a, labelB, b) in new[]
                 {
                     ("watched", watched, "output", output),
                     ("watched", watched, "work", work),
                     ("work", work, "output", output),
                 })
        {
            if (a is not null && b is not null && FoldersOverlap(a, b))
            {
                throw new ProcessingLibraryException(
                    $"This workflow's {labelA} folder and {labelB} folder overlap. Use separate folders, neither inside the other.");
            }
        }

        foreach (var other in others)
        {
            foreach (var (labelA, a) in new[] { ("watched", watched), ("output", output) })
            {
                foreach (var (labelB, raw) in new[] { ("watched", other.WatchedFolder), ("output", other.OutputFolder) })
                {
                    var b = NormalizeFolder(raw);
                    if (a is not null && b is not null && FoldersOverlap(a, b))
                    {
                        throw new ProcessingLibraryException(
                            $"This workflow's {labelA} folder overlaps the {labelB} folder of '{other.Name}'. " +
                            "Each workflow needs its own folders, neither inside another's.");
                    }
                }
            }
        }
    }
}
