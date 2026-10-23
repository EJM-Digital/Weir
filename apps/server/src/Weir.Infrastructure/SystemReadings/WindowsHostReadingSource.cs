using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Weir.Infrastructure.SystemReadings;

/// <summary>
/// The Windows readings: kernel calls for load, memory, disk counters and free space, and the registry for the system's
/// name and a pending restart.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostReadingSource : IHostReadingSource
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string ServicingRebootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
    private const string UpdateRebootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    private const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string PendingRenamesValue = "PendingFileRenameOperations";

    /// <summary>The first Windows 11 build; its registry still says "Windows 10" in the product name.</summary>
    private const int FirstWindows11Build = 22000;

    private readonly Func<RegistryKey> _openMachine;
    private readonly Func<IReadOnlyList<HostDrive>> _drives;
    private readonly Lazy<string?> _operatingSystem;

    public WindowsHostReadingSource()
        : this(null, null)
    {
    }

    /// <summary>The registry's machine key and the list of drives can be replaced, so a test can have either refuse.</summary>
    internal WindowsHostReadingSource(Func<RegistryKey>? openMachine, Func<IReadOnlyList<HostDrive>>? drives)
    {
        _openMachine = openMachine ?? (() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64));
        _drives = drives ?? ListDrives;
        _operatingSystem = new Lazy<string?>(ReadOperatingSystemName);
    }

    public CpuTimes? ReadCpuTimes() => Guarded(WindowsNative.ReadCpuTimes);

    public MemoryReading? ReadMemory() => Guarded(WindowsNative.ReadMemory);

    public long? ReadUptimeSeconds() => Environment.TickCount64 / 1000;

    public string? ReadOperatingSystem() => _operatingSystem.Value;

    public bool? ReadRebootPending()
    {
        try
        {
            using var machine = _openMachine();
            return KeyExists(machine, ServicingRebootKey)
                || KeyExists(machine, UpdateRebootKey)
                || HasPendingRenames(machine);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public DiskSnapshot ReadDisks()
    {
        var volumes = new Dictionary<string, DiskCounters>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in _drives())
            {
                if (drive.Type == DriveType.Fixed && drive.IsReady()
                    && WindowsNative.ReadVolumeCounters(drive.RootPath) is { } counters)
                {
                    volumes[KeyOf(drive.RootPath)] = counters;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DiskSnapshot.Empty;
        }

        return DiskSnapshot.FromVolumes(volumes);
    }

    public string? DriveRootOf(string path)
    {
        try
        {
            // A full path on Windows always has a root: a drive, a share or a device.
            return Path.GetPathRoot(Path.GetFullPath(path))!.TrimEnd('\\') + '\\';
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public string? VolumeKeyOf(string driveRoot)
    {
        if (driveRoot.StartsWith(@"\\", StringComparison.Ordinal) || !driveRoot.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new DriveInfo(driveRoot).DriveType == DriveType.Fixed ? KeyOf(driveRoot) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public DriveSpace? ReadSpace(string driveRoot) => Guarded(() => WindowsNative.ReadSpace(driveRoot));

    /// <summary>One drive as the disk readings see it. Whether it is ready is asked only of a fixed drive, because asking a removable one can stall.</summary>
    internal sealed record HostDrive(DriveType Type, Func<bool> IsReady, string RootPath);

    private static HostDrive[] ListDrives() =>
        [.. DriveInfo.GetDrives().Select(drive => new HostDrive(drive.DriveType, () => drive.IsReady, drive.RootDirectory.FullName))];

    private static string KeyOf(string root) => root.TrimEnd('\\', '/').ToUpperInvariant();

    /// <summary>Runs a kernel call, answering null on a system where the call is not there.</summary>
    internal static T? Guarded<T>(Func<T?> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static bool KeyExists(RegistryKey machine, string path)
    {
        using var key = machine.OpenSubKey(path);
        return key is not null;
    }

    private static bool HasPendingRenames(RegistryKey machine)
    {
        using var key = machine.OpenSubKey(SessionManagerKey);
        return HasEntries(key?.GetValue(PendingRenamesValue));
    }

    /// <summary>Whether a registry value lists anything: a multi-string with entries, or a string that is not empty.</summary>
    internal static bool HasEntries(object? value) => value is Array { Length: > 0 } or string { Length: > 0 };

    private string? ReadOperatingSystemName()
    {
        try
        {
            using var machine = _openMachine();
            using var key = machine.OpenSubKey(CurrentVersionKey);
            if (NameOf(key?.GetValue("ProductName"), key?.GetValue("CurrentBuildNumber")) is { } name)
            {
                return name;
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Falls through to the framework's description.
        }

        return RuntimeInformation.OSDescription;
    }

    /// <summary>The product name the registry gives, with Windows 11 called that from its first build; null when it gives none.</summary>
    internal static string? NameOf(object? productName, object? buildNumber)
    {
        if (productName is not string { Length: > 0 } product)
        {
            return null;
        }

        var build = int.TryParse(buildNumber as string, CultureInfo.InvariantCulture, out var number) ? number : 0;
        return build >= FirstWindows11Build ? product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : product;
    }
}
