namespace Weir.Infrastructure.Processing;

/// <summary>
/// Whether two folders are on the same filesystem, so a file can be renamed from one to the other instead of copied.
/// </summary>
/// <remarks>
/// On Linux this is decided by mount point, not by device: <c>rename()</c> refuses to cross mount points even when both
/// mount the same filesystem, which is exactly the case of two Docker bind mounts. On Windows it is decided by volume
/// root. Anywhere else the answer is unknown.
/// </remarks>
public static class FilesystemBoundaries
{
    private const string MountInfoPath = "/proc/self/mountinfo";

    /// <summary>The field of a <c>mountinfo</c> line that holds the mount point.</summary>
    private const int MountPointField = 4;

    /// <summary>True or false when it can tell, <see langword="null"/> when it cannot.</summary>
    public static bool? SameFilesystem(string first, string second)
    {
        ArgumentException.ThrowIfNullOrEmpty(first);
        ArgumentException.ThrowIfNullOrEmpty(second);
        var a = Path.GetFullPath(first);
        var b = Path.GetFullPath(second);
        if (OperatingSystem.IsWindows())
        {
            return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
        }

        // Anywhere else there is no mount table to read, which comes out the same as one that cannot be read.
        return SameMount(a, b, () => File.ReadAllText(MountInfoPath));
    }

    /// <summary>Whether two full paths are on one mount of the table <paramref name="readMountInfo"/> reads; null when it cannot be read or lists none.</summary>
    internal static bool? SameMount(string a, string b, Func<string> readMountInfo)
    {
        IReadOnlyList<string> mountPoints;
        try
        {
            mountPoints = ParseMountPoints(readMountInfo());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return mountPoints.Count == 0
            ? null
            : string.Equals(MountPointOf(a, mountPoints), MountPointOf(b, mountPoints), StringComparison.Ordinal);
    }

    /// <summary>The mount points listed in <c>/proc/self/mountinfo</c>, with its octal escapes (<c>\040</c> for a space) undone.</summary>
    internal static IReadOnlyList<string> ParseMountPoints(string mountInfo)
    {
        ArgumentNullException.ThrowIfNull(mountInfo);
        var result = new List<string>();
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length > MountPointField)
            {
                result.Add(Unescape(fields[MountPointField]));
            }
        }

        return result;
    }

    /// <summary>The deepest mount point that <paramref name="path"/> is at or under.</summary>
    internal static string MountPointOf(string path, IReadOnlyList<string> mountPoints)
    {
        ArgumentNullException.ThrowIfNull(mountPoints);
        var best = "/";
        foreach (var mountPoint in mountPoints)
        {
            var under = path == mountPoint || mountPoint == "/" ||
                path.StartsWith(mountPoint.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (under && mountPoint.Length > best.Length)
            {
                best = mountPoint;
            }
        }

        return best;
    }

    private static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
        {
            return field;
        }

        var text = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length && IsOctal(field[i + 1]) && IsOctal(field[i + 2]) && IsOctal(field[i + 3]))
            {
                text.Append((char)(((field[i + 1] - '0') * 64) + ((field[i + 2] - '0') * 8) + (field[i + 3] - '0')));
                i += 3;
            }
            else
            {
                text.Append(field[i]);
            }
        }

        return text.ToString();
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';
}
