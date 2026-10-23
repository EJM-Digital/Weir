using System.Globalization;
using System.Security;
using Microsoft.Data.Sqlite;

namespace Weir.Infrastructure.Runtime;

/// <summary>The backup folder does not have the room a copy needs.</summary>
internal sealed class NotEnoughRoomException : IOException
{
    public NotEnoughRoomException(long neededBytes, long freeBytes)
        : base($"A copy needs about {neededBytes} bytes and {freeBytes} are free.")
    {
        NeededBytes = neededBytes;
        FreeBytes = freeBytes;
    }

    public long NeededBytes { get; }

    public long FreeBytes { get; }
}

/// <summary>
/// Words a failed copy for the person: what stopped it and what to do about it, in plain language and never the exception's own
/// text, which goes to the log (docs/operator-messaging-standard.md). The kinds are the ones a person can act on: no room,
/// no permission, a file in use, and anything else.
/// </summary>
internal static class PreUpdateBackupFailure
{
    // Windows: ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL; ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION. Unix: ENOSPC.
    private const int WindowsDiskFull = 112;
    private const int WindowsHandleDiskFull = 39;
    private const int WindowsSharingViolation = 32;
    private const int WindowsLockViolation = 33;
    private const int UnixNoSpace = 28;

    // SQLite result codes.
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteReadOnly = 8;
    private const int SqliteFull = 13;
    private const int SqliteCantOpen = 14;
    private const int SqlitePermission = 3;

    private const long Megabyte = 1024L * 1024;
    private const long Gigabyte = 1024L * Megabyte;

    private enum Kind
    {
        NoRoom,
        NoPermission,
        InUse,
        Other,
    }

    /// <summary>One sentence or two for the person: what stopped the copy, and what to do next.</summary>
    internal static string Describe(Exception failure, string folder)
    {
        var volume = VolumeOf(folder);
        return KindOf(failure) switch
        {
            Kind.NoRoom when Find<NotEnoughRoomException>(failure) is { } room =>
                $"{Capitalised(volume)} needs about {SizeText(room.NeededBytes)} free to hold the copy and has {SizeText(room.FreeBytes)}. " +
                "Free up space there, then try again.",
            Kind.NoRoom => $"{Capitalised(volume)} is full. Free up some space there, then try again.",
            Kind.NoPermission =>
                $"Weir isn't allowed to write to {folder}. Give the account Weir runs as full access to that folder, then try again.",
            Kind.InUse => "Another program is using Weir's database or its backup folder. Close it, then try again.",
            _ => "Something unexpected went wrong. The details are in Weir's log files.",
        };
    }

    /// <summary>A size as a person reads it: "480 MB", "1.5 GB". A size under a megabyte reads as one.</summary>
    internal static string SizeText(long bytes) => bytes >= Gigabyte
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)Gigabyte:0.#} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (bytes + Megabyte - 1) / Megabyte)} MB");

    private static Kind KindOf(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case NotEnoughRoomException:
                    return Kind.NoRoom;
                case SqliteException sqlite:
                    if (SqliteKind(sqlite.SqliteErrorCode) is { } sqliteKind)
                    {
                        return sqliteKind;
                    }

                    break;
                case UnauthorizedAccessException or SecurityException:
                    return Kind.NoPermission;
                case IOException io:
                    if (IOKind(io.HResult) is { } ioKind)
                    {
                        return ioKind;
                    }

                    break;
            }
        }

        return Kind.Other;
    }

    private static Kind? SqliteKind(int code) => code switch
    {
        SqliteFull => Kind.NoRoom,
        SqliteBusy or SqliteLocked => Kind.InUse,
        SqlitePermission or SqliteReadOnly or SqliteCantOpen => Kind.NoPermission,
        _ => null,
    };

    private static Kind? IOKind(int hResult)
    {
        // A Windows error comes as 0x8007xxxx; a Unix errno as itself.
        var code = (hResult & 0xFFFF0000) == 0x80070000 ? hResult & 0xFFFF : hResult;
        return code switch
        {
            WindowsDiskFull or WindowsHandleDiskFull or UnixNoSpace => Kind.NoRoom,
            WindowsSharingViolation or WindowsLockViolation => Kind.InUse,
            _ => null,
        };
    }

    private static T? Find<T>(Exception failure)
        where T : Exception
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>The drive or disk the folder is on, as a phrase: "drive C:", or "the disk that holds /data/weir/backups".</summary>
    private static string VolumeOf(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            // A full path on Windows always has a root: a drive ("C:\") or a share ("\\server\share").
            var root = Path.GetPathRoot(Path.GetFullPath(folder))!;
            return root[1] == ':' ? $"drive {root[..2]}" : $"the share {root.TrimEnd('\\')}";
        }

        return $"the disk that holds {folder}";
    }

    private static string Capitalised(string phrase) => string.Concat(char.ToUpperInvariant(phrase[0]), phrase[1..]);
}
