using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Weir.Core.Json;

namespace Weir.Infrastructure.Browse;

/// <summary>A failure the endpoint answers with this status and detail.</summary>
public sealed class DirectoryBrowseException : Exception
{
    public DirectoryBrowseException()
    {
    }

    public DirectoryBrowseException(string message)
        : base(message)
    {
    }

    public DirectoryBrowseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DirectoryBrowseException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public DirectoryBrowseException(int statusCode, string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>Server-side folder browsing for path settings.</summary>
public static partial class DirectoryBrowser
{
    public static WireObject Browse(string? path) => Browse(path, Directory.EnumerateFileSystemEntries);

    /// <summary>Browses with the given way of listing a folder's entries, so a failure while listing can be shown without a damaged disk.</summary>
    internal static WireObject Browse(string? path, Func<string, IEnumerable<string>> listEntries)
    {
        if (path is null || path.Trim().Length == 0)
        {
            return Out(null, null, RootEntries());
        }

        var resolved = ResolveOnAllowedRoot(path);
        var normalized = OperatingSystem.IsWindows() ? WithTrailingSeparator(resolved) : resolved;
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryBrowseException(404, "The requested directory does not exist.");
        }

        var parentPath = ParentOf(normalized);
        var entries = new List<(string Name, WireObject Entry)>();
        try
        {
            foreach (var child in listEntries(normalized))
            {
                if (!Directory.Exists(child))
                {
                    continue;
                }

                var name = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar));
                var childPath = RealPath(child);
                entries.Add((name, new WireObject()
                    .Set("name", name)
                    .Set("path", OperatingSystem.IsWindows() ? WithTrailingSeparator(childPath) : childPath)
                    .Set("kind", "directory")
                    .Set("description", (string?)null)));
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DirectoryBrowseException(403, "Weir cannot access this folder.", exception);
        }
        catch (IOException exception)
        {
            throw new DirectoryBrowseException(400, "The requested directory cannot be accessed.", exception);
        }

        var sorted = entries.OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal).Select(e => e.Entry).ToList();
        return Out(normalized, parentPath, sorted);
    }

    /// <summary>
    /// Validates an absolute file path against the same allow-list <see cref="Browse(string?)"/> applies to
    /// directories (an existing drive root on Windows, the filesystem root elsewhere). Used where a
    /// caller lets the operator pick any file the local file picker could reach, rather than one
    /// scoped to a single saved folder (issue #502's "Try on a file" preview). Returns the resolved
    /// realpath; throws <see cref="DirectoryBrowseException"/> with a plain detail otherwise.
    /// </summary>
    public static string ValidateFilePath(string? path)
    {
        if (path is null || path.Trim().Length == 0)
        {
            throw new DirectoryBrowseException(400, "A file path is required.");
        }

        var normalized = ResolveOnAllowedRoot(path);
        if (!File.Exists(normalized))
        {
            throw new DirectoryBrowseException(404, "The requested file does not exist.");
        }

        return normalized;
    }

    /// <summary>
    /// Rejects a path's dangerous forms before it reaches any filesystem call (<see cref="RealPath"/>'s
    /// <c>CreateFileW</c> among them): a UNC path (<c>\\</c> or <c>//</c>), a Windows device path (which
    /// always starts the same way, so the same check covers <c>\\?\</c> and <c>\\.\</c>), and an alternate
    /// data stream (a colon after the drive letter). Stateless, and shared by every entry point that turns
    /// operator-supplied text into a path.
    /// </summary>
    internal static void RejectUnsafePathForm(string path)
    {
        var slashUnified = path.Replace('/', '\\');
        if (slashUnified.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new DirectoryBrowseException(400, "UNC and device paths are not allowed.");
        }

        // Alternate data streams are an NTFS concept; a colon is an ordinary, legal filename character
        // on Linux/macOS, so this check only applies where a drive letter does.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hasDriveLetter = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        if (path.IndexOf(':', hasDriveLetter ? 2 : 0) >= 0)
        {
            throw new DirectoryBrowseException(400, "Alternate data streams are not allowed.");
        }
    }

    /// <summary>The link-resolved absolute path for operator-supplied text, or a 400 <see cref="DirectoryBrowseException"/> when it is unsafe, relative or off every allowed root.</summary>
    private static string ResolveOnAllowedRoot(string path)
    {
        var sanitized = path.Replace("\0", string.Empty, StringComparison.Ordinal);
        RejectUnsafePathForm(sanitized);
        if (!IsAbsolute(sanitized))
        {
            throw new DirectoryBrowseException(400, "Path must be absolute.");
        }

        var resolved = RealPath(sanitized);
        if (OperatingSystem.IsWindows() && !IsOnDriveRoot(WithTrailingSeparator(resolved), ValidWindowsRoots()))
        {
            throw new DirectoryBrowseException(400, "Path is not within a valid drive root.");
        }

        return resolved;
    }

    private static bool IsOnDriveRoot(string pathWithSeparator, IEnumerable<string> roots) =>
        roots.Any(root => pathWithSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private static string WithTrailingSeparator(string path) => path.TrimEnd('\\', '/') + "\\";

    private static WireObject Out(string? current, string? parent, IEnumerable<WireObject> entries) => new WireObject()
        .Set("current_path", current)
        .Set("parent_path", parent)
        .Set("entries", new WireArray(entries));

    private static List<WireObject> RootEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [Entry("/", "/", "root", "Filesystem root")];
        }

        return [.. ValidWindowsRoots().Select(root => Entry(root.TrimEnd('\\'), root, "root", DescribeDrive(new DriveInfo(root).DriveType)))];
    }

    internal static string DescribeDrive(DriveType type) => type switch
    {
        DriveType.Removable => "External drive",
        DriveType.Fixed => "Local drive",
        DriveType.Network => "Network drive",
        DriveType.CDRom => "Optical drive",
        DriveType.Ram => "RAM disk",
        _ => "Drive",
    };

    private static WireObject Entry(string name, string path, string kind, string? description) => new WireObject()
        .Set("name", name)
        .Set("path", path)
        .Set("kind", kind)
        .Set("description", description);

    private static List<string> ValidWindowsRoots() =>
        [.. Enumerable.Range('A', 26).Select(c => $"{(char)c}:\\").Where(Directory.Exists)];

    /// <summary>Whether the path is absolute; on Windows a leading separator counts as absolute too.</summary>
    private static bool IsAbsolute(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path.StartsWith('/');
        }

        var head = path.Length > 3 ? path[..3] : path;
        head = head.Replace('/', '\\');
        return head.StartsWith('\\') || (head.Length >= 3 && head[1..3] == ":\\");
    }

    /// <summary>The parent directory, or null for a root.</summary>
    private static string? ParentOf(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var trimmed = path.TrimEnd('\\');
            return trimmed.EndsWith(':') ? null : WithTrailingSeparator(Path.GetDirectoryName(trimmed)!);
        }

        return path == "/" ? null : Path.GetDirectoryName(path);
    }

    /// <summary>The real path: absolute, normalised, links resolved where the path exists.</summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return WindowsFinalPath(full) ?? TrimTrailing(full);
        }

        return UnixRealPath(full);
    }

    private static string TrimTrailing(string full)
    {
        var root = Path.GetPathRoot(full)!;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    private static string UnixRealPath(string full)
    {
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "/";
        foreach (var part in parts)
        {
            var next = current == "/" ? "/" + part : current + "/" + part;
            try
            {
                var info = new FileInfo(next);
                if (info.LinkTarget is not null)
                {
                    next = Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)!.FullName);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unresolvable links stay as written rather than failing the whole path.
            }

            current = next;
        }

        return current;
    }

    private static string? WindowsFinalPath(string full)
    {
        const uint fileReadAttributes = 0x80;
        const uint shareAll = 0x7;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFileW(full, fileReadAttributes, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        // 32,768 characters is the longest path Windows allows, so one call always fits.
        unsafe
        {
            var buffer = stackalloc char[32768];
            var length = GetFinalPathNameByHandleW(handle, buffer, 32768, 0);
            return length == 0 ? null : TrimTrailing(WithoutDevicePrefix(new string(buffer, 0, (int)length)));
        }
    }

    /// <summary>A final path without the <c>\\?\</c> device prefix, a network share written as an ordinary <c>\\server\share</c> path.</summary>
    internal static string WithoutDevicePrefix(string finalPath)
    {
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + finalPath[8..];
        }

        return finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..] : finalPath;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint pathLength, uint flags);
}
