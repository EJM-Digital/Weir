using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Weir.Infrastructure.SystemReadings;

/// <summary>
/// The documented kernel32 calls Weir reads the machine with: processor time, physical memory, a volume's disk counters
/// and a drive's free space. No performance counters and no WMI, because counter names are localised and the services
/// behind them can be switched off.
/// </summary>
internal static partial class WindowsNative
{
    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint NoAccess = 0;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    /// <summary>
    /// DISK_PERFORMANCE. The trailing storage-manager name is four raw words rather than a string: the buffer must be
    /// exactly as large as the native struct, or the call refuses it as an invalid parameter.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DiskPerformance
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        public uint StorageManagerName0;
        public uint StorageManagerName1;
        public uint StorageManagerName2;
        public uint StorageManagerName3;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        out DiskPerformance outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalBytes,
        out ulong totalFreeBytes);

    /// <summary>Processor time of the whole machine since boot, in 100 ns ticks. Kernel time includes idle, so the total is kernel plus user.</summary>
    public static CpuTimes? ReadCpuTimes()
    {
        var succeeded = GetSystemTimes(out var idle, out var kernel, out var user);
        return CpuTimesFrom(succeeded, idle.Value, kernel.Value, user.Value);
    }

    /// <summary>Test seam (<c>InternalsVisibleTo</c> to Weir.Infrastructure.Tests): what Windows' answer means, so a refusal can be run on any system.</summary>
    internal static CpuTimes? CpuTimesFrom(bool succeeded, ulong idle, ulong kernel, ulong user) =>
        succeeded ? new CpuTimes(idle, kernel + user) : null;

    public static MemoryReading? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var succeeded = GlobalMemoryStatusEx(ref status);
        return MemoryFrom(succeeded, status.TotalPhysical, status.AvailablePhysical);
    }

    /// <summary>Test seam: what Windows' answer about memory means.</summary>
    internal static MemoryReading? MemoryFrom(bool succeeded, ulong total, ulong available) =>
        succeeded ? new MemoryReading((long)total, (long)available) : null;

    /// <summary>
    /// Size and free space under <paramref name="path"/>, which may be a UNC share. Free space is what the calling user
    /// can use, so a quota is respected.
    /// </summary>
    public static DriveSpace? ReadSpace(string path) =>
        GetDiskFreeSpaceExW(path, out var available, out var total, out _)
            ? new DriveSpace((long)Math.Min(total, long.MaxValue), (long)Math.Min(available, long.MaxValue))
            : null;

    /// <summary>
    /// A local volume's load, straight from the volume. Opening it needs no elevation and no counter service, but a
    /// locked, missing or network volume refuses, hence null rather than an exception. <paramref name="volumeRoot"/> is a
    /// drive root such as <c>C:\</c>.
    /// </summary>
    public static DiskCounters? ReadVolumeCounters(string volumeRoot)
    {
        if (string.IsNullOrWhiteSpace(volumeRoot) || !volumeRoot.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var device = @"\\.\" + volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Zero access is enough to ask for counters and needs no administrator; GENERIC_READ covers a system that rejects it.
        foreach (var access in new[] { NoAccess, GenericRead })
        {
            using var handle = CreateFileW(device, access, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                continue;
            }

            var succeeded = DeviceIoControl(handle, IoctlDiskPerformance, IntPtr.Zero, 0, out var performance, (uint)Marshal.SizeOf<DiskPerformance>(), out _, IntPtr.Zero);
            return CountersFrom(succeeded, performance.BytesRead, performance.BytesWritten, performance.IdleTime, performance.QueryTime);
        }

        return null;
    }

    /// <summary>Test seam: what Windows' answer about a volume's load means.</summary>
    internal static DiskCounters? CountersFrom(bool succeeded, long bytesRead, long bytesWritten, long idleTime, long queryTime) =>
        succeeded ? new DiskCounters(bytesRead, bytesWritten, idleTime, queryTime) : null;
}
