using System.Runtime.Versioning;
using Weir.Infrastructure.SystemReadings;

namespace Weir.Infrastructure.Tests.SystemReadings;

/// <summary>
/// What Windows itself says: the kernel's memory, processor and disk counters, the registry's name for the system, and the size of a
/// drive, read from the machine the test runs on and held to what has to be true of any Windows machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostReadingSourceTests
{
    private const string OnlyWindows = "These are the Windows kernel's and registry's own answers.";

    private readonly WindowsHostReadingSource _source = new();

    private static string TempRoot => Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

    [WindowsFact(OnlyWindows)]
    public void Memory_is_the_machines_and_what_is_free_is_part_of_it()
    {
        var memory = _source.ReadMemory();

        Assert.NotNull(memory);
        Assert.True(memory.Value.Total > Environment.WorkingSet, "The machine holds more than this one process does.");
        Assert.InRange(memory.Value.Available, 0, memory.Value.Total);
        Assert.Equal(memory.Value.Total - memory.Value.Available, memory.Value.Used);
    }

    [WindowsFact(OnlyWindows)]
    public void Processor_time_never_goes_backwards_and_idle_is_part_of_it()
    {
        var first = _source.ReadCpuTimes();
        var second = _source.ReadCpuTimes();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.InRange(first.Value.Idle, 0UL, first.Value.Total);
        Assert.True(first.Value.Total > 0);
        Assert.True(second.Value.Total >= first.Value.Total);
        Assert.True(second.Value.Idle >= first.Value.Idle);
    }

    [WindowsFact(OnlyWindows)]
    public void Uptime_is_the_time_since_the_machine_started()
    {
        var uptime = _source.ReadUptimeSeconds();

        Assert.NotNull(uptime);
        Assert.InRange(uptime.Value, 1, Environment.TickCount64 / 1000 + 1);
    }

    [WindowsFact(OnlyWindows)]
    public void The_system_is_named_as_a_person_would_say_it_and_Windows_11_is_not_called_Windows_10()
    {
        var name = _source.ReadOperatingSystem();

        Assert.NotNull(name);
        Assert.StartsWith("Windows", name, StringComparison.Ordinal);
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            Assert.DoesNotContain("Windows 10", name, StringComparison.Ordinal);
            // Windows Server shares the build numbers of Windows 11 and is named for what it is.
            Assert.Contains(name.Contains("Server", StringComparison.Ordinal) ? "Windows Server" : "Windows 11", name, StringComparison.Ordinal);
        }
    }

    [WindowsFact(OnlyWindows)]
    public void A_pending_restart_is_answered_yes_or_no_without_needing_an_administrator()
    {
        var pending = _source.ReadRebootPending();

        Assert.True(pending.HasValue, "The three places Windows records a pending restart can be read by any signed-in account.");
    }

    [WindowsFact(OnlyWindows)]
    public void The_drive_of_a_path_is_its_root_and_a_share_is_a_drive_that_has_no_counters()
    {
        Assert.Equal(TempRoot, _source.DriveRootOf(Path.Join(Path.GetTempPath(), "a", "b.mkv")));
        Assert.Equal(@"\\nas\media\", _source.DriveRootOf(@"\\nas\media\films\a.mkv"));
        Assert.Null(_source.DriveRootOf(string.Empty));
        Assert.Null(_source.VolumeKeyOf(@"\\nas\media\"));
    }

    [WindowsFact(OnlyWindows)]
    public void The_disk_counters_of_the_drive_a_folder_is_on_are_listed_and_only_ever_count_up()
    {
        var key = _source.VolumeKeyOf(_source.DriveRootOf(Path.GetTempPath())!);
        Assert.NotNull(key);

        var first = _source.ReadDisks();
        var second = _source.ReadDisks();

        Assert.True(first.Volumes.ContainsKey(key), "The drive Weir's temp folder is on is a local drive with counters.");
        Assert.NotNull(first.Machine);
        Assert.True(second.Volumes[key].BytesRead >= first.Volumes[key].BytesRead);
        Assert.True(second.Volumes[key].BytesWritten >= first.Volumes[key].BytesWritten);
        Assert.True(first.Machine.BytesRead >= first.Volumes[key].BytesRead);
    }

    [WindowsFact(OnlyWindows)]
    public void The_size_of_a_drive_is_what_Windows_says_it_is_and_free_space_is_part_of_it()
    {
        var root = _source.DriveRootOf(Path.GetTempPath())!;

        var space = _source.ReadSpace(root);

        Assert.NotNull(space);
        Assert.Equal(new DriveInfo(root).TotalSize, space.Value.Total);
        Assert.InRange(space.Value.Free, 0, space.Value.Total);
    }
}
