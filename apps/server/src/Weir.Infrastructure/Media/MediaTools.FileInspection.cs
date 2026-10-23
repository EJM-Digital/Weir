using Microsoft.Extensions.Logging;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Media;

/// <summary>The file facts <see cref="MediaTools.FfprobeJsonAsync"/> logs and checks: resolved path, existence, size and mtime (seconds since the epoch).</summary>
internal readonly record struct MediaFileState(string ResolvedPath, bool Exists, bool IsFile, long SizeBytes, double MtimeEpoch);

/// <summary>What a probe reads about a file before running ffprobe on it.</summary>
public sealed partial class MediaTools
{
    /// <summary>What <see cref="FfprobeJsonAsync"/> reads about the file before probing it.</summary>
    internal static MediaFileState InspectFile(string path) => InspectFile(path, PhysicalFileSystem.Instance);

    /// <summary><see cref="InspectFile(string)"/> reading the file through <paramref name="files"/>.</summary>
    internal static MediaFileState InspectFile(string path, IFileSystem files)
    {
        var resolvedPath = ResolvePath(path);
        var exists = files.FileExists(path) || files.DirectoryExists(path);
        var isFile = files.FileExists(path);
        long size;
        double mtime;
        try
        {
            if (isFile)
            {
                size = files.GetLength(path);
                mtime = (files.GetLastWriteTimeUtc(path) - DateTime.UnixEpoch).TotalSeconds;
            }
            else if (exists)
            {
                size = 0;
                mtime = (files.GetDirectoryLastWriteTimeUtc(path) - DateTime.UnixEpoch).TotalSeconds;
            }
            else
            {
                size = -1;
                mtime = 0.0;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            size = -1;
            mtime = 0.0;
        }


        return new MediaFileState(resolvedPath, exists, isFile, size, mtime);
    }

    /// <summary>The size of a file a tool is about to read, which sizes how long the tool is given; 0 when it cannot be read.</summary>
    internal long SizeOfFile(string path)
    {
        if (_inspectFile(path) is { IsFile: true, SizeBytes: > 0 } state)
        {
            return state.SizeBytes;
        }

        LogSizeUnknown(path);
        return 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Weir could not read the size of {Path}, so its media tool is given the least time instead of a time sized to the file.")]
    private partial void LogSizeUnknown(string path);

    private static string ResolvePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
