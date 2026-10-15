using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>
/// Minimal path joining for a download client's own folder layout. This is the client's OS, not the one Weir
/// runs on, so <see cref="System.IO.Path"/> (which follows the running OS's rules) cannot be used.
/// </summary>
public static class DownloadClientPaths
{
    /// <summary>A Unix root, a Windows drive letter, or a UNC root.</summary>
    public static bool IsAbsolute(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (path[0] is '/' or '\\')
        {
            return true;
        }

        return path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);
    }

    /// <summary>Whether a path has a <c>..</c> segment, in either style of separator: one that would climb out of the folder it is read under.</summary>
    public static bool ClimbsOut(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Split('/', '\\').Contains("..", StringComparer.Ordinal);
    }

    /// <summary>
    /// Joins a relative path onto a base folder, using whichever separator the base folder already uses. The relative path is the download
    /// client's own text, so one with a <c>..</c> segment is refused rather than joined: it would name a place outside the base folder.
    /// </summary>
    public static DownloadClientJoin Join(string basePath, string relative)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(relative);
        if (ClimbsOut(relative))
        {
            return new DownloadClientJoin(
                null,
                $"The download client gave the folder {WireStrings.Repr(relative)}, which climbs out of its completed-downloads folder with '..'. Weir did not use it.");
        }

        var separator = basePath.Contains('\\') && !basePath.Contains('/') ? '\\' : '/';
        var trimmedBase = basePath.TrimEnd('/', '\\');
        var trimmedRelative = relative.TrimStart('/', '\\');
        return new DownloadClientJoin($"{trimmedBase}{separator}{trimmedRelative}", null);
    }
}

/// <summary>Either the folder a download client's relative folder makes under its base folder, or the reason it cannot be used.</summary>
public sealed record DownloadClientJoin(string? Path, string? Problem)
{
    public bool Ok => Path is not null;
}
