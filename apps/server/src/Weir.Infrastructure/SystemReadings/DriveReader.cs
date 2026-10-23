using Weir.Core.Jobs;

namespace Weir.Infrastructure.SystemReadings;

/// <summary>
/// Reads each drive that holds a workflow's folders: its size and free space (network shares included), how much of it is
/// Weir's own work files, how fast it is filling, and how busy it is. Disk activity needs two readings to compare, so this
/// holds the earlier one and is meant to be called by one caller, the sampler, at a steady pace.
/// </summary>
public sealed class DriveReader
{
    private readonly IHostReadingSource _source;
    private readonly FreeSpaceForecast _forecast;
    private readonly TimeProvider _time;
    private readonly Func<string, long> _workFileBytes;
    private DateTimeOffset _previousAt;
    private DiskSnapshot _previousDisks = DiskSnapshot.Empty;

    public DriveReader(IHostReadingSource source, FreeSpaceForecast forecast, TimeProvider time, Func<string, long>? workFileBytes = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(time);
        _source = source;
        _forecast = forecast;
        _time = time;
        _workFileBytes = workFileBytes ?? WorkFileBytes;
    }

    public IReadOnlyList<DriveReading> Read(IReadOnlyList<WorkflowFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var now = _time.GetUtcNow();
        var seconds = _previousAt == default ? 0 : (now - _previousAt).TotalSeconds;
        var disks = _source.ReadDisks();

        var drives = new List<DriveReading>();
        foreach (var onDrive in folders
            .Select(folder => (Root: _source.DriveRootOf(folder.Path), Folder: folder))
            .Where(entry => entry.Root is not null)
            .GroupBy(entry => entry.Root!, entry => entry.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (_source.ReadSpace(onDrive.Key) is { } space)
            {
                drives.Add(Describe(onDrive.Key, onDrive.ToList(), space, disks, seconds));
            }
        }

        _previousAt = now;
        _previousDisks = disks;
        return drives;
    }

    private DriveReading Describe(string root, List<WorkflowFolder> folders, DriveSpace space, DiskSnapshot disks, double seconds)
    {
        var key = _source.VolumeKeyOf(root);
        var activity = key is not null && seconds > 0
            ? HostRates.DiskActivityBetween(_previousDisks.Volumes.GetValueOrDefault(key), disks.Volumes.GetValueOrDefault(key), seconds)
            : null;
        var workFolders = folders.Where(folder => folder.Role == WorkflowFolders.Work).Select(folder => folder.Path).Distinct(StringComparer.OrdinalIgnoreCase);
        var workflows = folders
            .GroupBy(folder => folder.WorkflowId)
            .Select(group => new WorkflowOnDrive(
                group.Key,
                group.First().WorkflowName,
                [.. group.Select(folder => folder.Role).Distinct().OrderBy(RoleOrder)]))
            .OrderBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DriveReading(
            root.TrimEnd('\\'),
            root,
            space.Total,
            space.Free,
            workFolders.Sum(_workFileBytes),
            folders.Max(folder => folder.KeepFreeBytes),
            _forecast.Record(root, space.Free),
            activity?.ReadBytesPerSecond,
            activity?.WriteBytesPerSecond,
            activity?.BusyPercent,
            workflows);
    }

    private static int RoleOrder(string role) => role switch
    {
        WorkflowFolders.Watched => 0,
        WorkflowFolders.Work => 1,
        _ => 2,
    };

    /// <summary>The size of the temporary files Weir has in a work folder; the folder may be missing or unreadable, which counts as none.</summary>
    private static long WorkFileBytes(string workFolder)
    {
        try
        {
            return Directory.EnumerateFiles(workFolder)
                .Where(file => WeirTempFiles.IsRemuxTempName(Path.GetFileName(file)))
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
