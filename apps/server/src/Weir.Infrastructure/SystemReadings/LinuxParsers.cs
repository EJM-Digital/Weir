using System.Globalization;

namespace Weir.Infrastructure.SystemReadings;

/// <summary>A mount from <c>/proc/self/mountinfo</c>: where it is attached and the device behind it as <c>major:minor</c>.</summary>
public sealed record Mount(string MountPoint, string Device);

/// <summary>Parsers for the /proc and /sys files, each a pure function of the file's text.</summary>
public static class LinuxParsers
{
    private const int SectorBytes = 512;
    private const int MinimumCpuColumns = 4;
    private const int CpuColumnsCounted = 8;
    private const int IdleColumn = 3;
    private const int IoWaitColumn = 4;
    private const int DiskStatColumns = 14;
    private const int DiskSectorsReadColumn = 5;
    private const int DiskSectorsWrittenColumn = 9;
    private const int DiskIoMillisecondsColumn = 12;
    private const int MountPointColumn = 4;
    private const int MountDeviceColumn = 2;
    private const int MountColumns = 5;
    private const long BytesPerKilobyte = 1024;
    private const string Unlimited = "max";

    /// <summary>The first line of /proc/stat: idle is idle plus iowait, total is the first eight columns (guest time is already inside user).</summary>
    public static CpuTimes? ParseCpuTimes(string? text)
    {
        var line = text?.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(CpuColumnsCounted)
            .Select(part => ulong.TryParse(part, CultureInfo.InvariantCulture, out var value) ? value : 0UL)
            .ToArray();
        if (columns.Length < MinimumCpuColumns)
        {
            return null;
        }

        var idle = columns[IdleColumn] + (columns.Length > IoWaitColumn ? columns[IoWaitColumn] : 0UL);
        return new CpuTimes(idle, columns.Aggregate(0UL, (sum, value) => sum + value));
    }

    /// <summary>MemTotal and MemAvailable from /proc/meminfo, in bytes.</summary>
    public static MemoryReading? ParseMemory(string? text)
    {
        long? total = null, available = null;
        foreach (var line in text?.Split('\n') ?? [])
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = Kilobytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = Kilobytes(line);
            }
        }

        return total is > 0 && available is not null ? new MemoryReading(total.Value, available.Value) : null;
    }

    public static long? ParseUptimeSeconds(string? text)
    {
        var first = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? (long)seconds
            : null;
    }

    public static string? ParseOsPrettyName(string? text)
    {
        foreach (var line in text?.Split('\n') ?? [])
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                var value = line["PRETTY_NAME=".Length..].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    /// <summary>A whole physical disk by name: not a loop, ram, device-mapper, md or zram device, which sit on top of one that is already counted.</summary>
    public static bool IsPhysicalDiskName(string name) =>
        !(name.StartsWith("loop", StringComparison.Ordinal)
          || name.StartsWith("ram", StringComparison.Ordinal)
          || name.StartsWith("dm-", StringComparison.Ordinal)
          || name.StartsWith("md", StringComparison.Ordinal)
          || name.StartsWith("zram", StringComparison.Ordinal)
          || name.StartsWith("sr", StringComparison.Ordinal)
          || name.StartsWith("fd", StringComparison.Ordinal));

    /// <summary>
    /// /proc/diskstats keyed by <c>major:minor</c>. Bytes are sectors of 512. Query time is the machine's uptime and idle
    /// time is that minus the time the disk spent doing I/O, so the part of a window that was not idle comes out the same
    /// way as on Windows.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Name, DiskCounters Counters)> ParseDiskStats(string? text, long uptimeMilliseconds)
    {
        var result = new Dictionary<string, (string, DiskCounters)>(StringComparer.Ordinal);
        foreach (var line in text?.Split('\n') ?? [])
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < DiskStatColumns)
            {
                continue;
            }

            long Field(int index) => long.TryParse(parts[index], CultureInfo.InvariantCulture, out var value) ? value : 0;

            result[$"{parts[0]}:{parts[1]}"] = (
                parts[2],
                new DiskCounters(
                    BytesRead: Field(DiskSectorsReadColumn) * SectorBytes,
                    BytesWritten: Field(DiskSectorsWrittenColumn) * SectorBytes,
                    IdleTime: Math.Max(0, uptimeMilliseconds - Field(DiskIoMillisecondsColumn)),
                    QueryTime: uptimeMilliseconds));
        }

        return result;
    }

    /// <summary>The mount in <c>/proc/self/mountinfo</c> that holds <paramref name="path"/>: the one whose mount point is the longest prefix of it.</summary>
    public static Mount? MountOf(string? mountInfo, string path)
    {
        if (string.IsNullOrWhiteSpace(mountInfo) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Length > 1 ? path.TrimEnd('/') : path;
        Mount? best = null;
        foreach (var line in mountInfo.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < MountColumns)
            {
                continue;
            }

            var mountPoint = Unescape(parts[MountPointColumn]);
            var holds = mountPoint == "/" || normalized == mountPoint || normalized.StartsWith(mountPoint + "/", StringComparison.Ordinal);
            if (holds && (best is null || mountPoint.Length >= best.MountPoint.Length))
            {
                best = new Mount(mountPoint, parts[MountDeviceColumn]);
            }
        }

        return best;
    }

    /// <summary>The cores in cgroup v2 <c>cpu.max</c> (<c>quota period</c>), or null when it says <c>max</c> or cannot be read.</summary>
    public static double? ParseCpuMaxCores(string? text)
    {
        var parts = text?.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 2 } || parts[0] == Unlimited
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var quota)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var period))
        {
            return null;
        }

        return quota > 0 && period > 0 ? quota / period : null;
    }

    /// <summary>The processor time the cgroup has used, in microseconds, from <c>cpu.stat</c>.</summary>
    public static long? ParseCpuUsageMicroseconds(string? text) => StatValue(text, "usage_usec");

    /// <summary>A cgroup v2 byte count or limit such as <c>memory.max</c> or <c>memory.current</c>; null when it says <c>max</c> or cannot be read.</summary>
    public static long? ParseBytes(string? text)
    {
        var value = text?.Trim();
        return !string.IsNullOrEmpty(value) && value != Unlimited && long.TryParse(value, CultureInfo.InvariantCulture, out var bytes) && bytes > 0
            ? bytes
            : null;
    }

    /// <summary>The page cache the kernel can take back at once, from <c>memory.stat</c>; it is not memory the container is holding on to.</summary>
    public static long? ParseInactiveFileBytes(string? text) => StatValue(text, "inactive_file");

    private static long? StatValue(string? text, string key)
    {
        foreach (var line in text?.Split('\n') ?? [])
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == key && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static long? Kilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var kilobytes) ? kilobytes * BytesPerKilobyte : null;
    }

    /// <summary>mountinfo writes a space as <c>\040</c>, a tab as <c>\011</c> and a backslash as <c>\134</c> inside a path.</summary>
    private static string Unescape(string value) =>
        value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
}
