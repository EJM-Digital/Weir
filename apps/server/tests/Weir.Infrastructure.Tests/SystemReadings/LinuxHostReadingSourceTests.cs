using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.SystemReadings;

namespace Weir.Infrastructure.Tests.SystemReadings;

/// <summary>What a Linux machine, and a Docker container on one, report: the host's numbers, or the container's own when it is limited.</summary>
public sealed class LinuxHostReadingSourceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));

    private LinuxHostReadingSource Over(FakeProcSource files) => new(files, _time);

    [Fact]
    public void Without_a_cgroup_limit_the_machine_is_the_host()
    {
        var files = new FakeProcSource()
            .With("/proc/stat", "cpu  100 0 100 700 100 0 0 0 0 0\n")
            .With("/proc/meminfo", "MemTotal: 1000 kB\nMemAvailable: 250 kB\n")
            .With("/sys/fs/cgroup/cpu.max", "max 100000\n")
            .With("/sys/fs/cgroup/memory.max", "max\n");

        var source = Over(files);

        Assert.Equal(new CpuTimes(800, 1000), source.ReadCpuTimes());
        Assert.Equal(new MemoryReading(1000 * 1024, 250 * 1024), source.ReadMemory());
    }

    [Fact]
    public void A_container_with_a_cpu_limit_reports_its_own_use_of_the_cores_it_is_given()
    {
        var files = new FakeProcSource()
            .With("/proc/stat", "cpu  1 0 1 1 0 0 0 0 0 0\n")
            .With("/sys/fs/cgroup/cpu.max", "200000 100000\n")
            .With("/sys/fs/cgroup/cpu.stat", "usage_usec 40000000\n");
        var source = Over(files);
        var first = source.ReadCpuTimes()!.Value;

        _time.Advance(TimeSpan.FromSeconds(10));
        files.With("/sys/fs/cgroup/cpu.stat", "usage_usec 45000000\n");
        var second = source.ReadCpuTimes()!.Value;

        // Two cores for ten seconds allow twenty seconds of work, and the container used five.
        Assert.Equal(25.0, HostRates.CpuPercent(first, second));
    }

    [Fact]
    public void A_container_with_a_memory_limit_reports_what_it_holds_against_that_limit_less_what_the_kernel_can_take_back()
    {
        var files = new FakeProcSource()
            .With("/proc/meminfo", "MemTotal: 99999999 kB\nMemAvailable: 99999999 kB\n")
            .With("/sys/fs/cgroup/memory.max", "1000000\n")
            .With("/sys/fs/cgroup/memory.current", "600000\n")
            .With("/sys/fs/cgroup/memory.stat", "anon 1\ninactive_file 100000\n");

        var memory = Over(files).ReadMemory();

        Assert.Equal(new MemoryReading(Total: 1_000_000, Available: 500_000), memory);
    }

    [Fact]
    public void A_machine_that_serves_no_stat_files_reports_nothing()
    {
        var source = Over(new FakeProcSource());

        Assert.Null(source.ReadCpuTimes());
        Assert.Null(source.ReadMemory());
        Assert.Null(source.ReadOperatingSystem());
        Assert.Equal(DiskSnapshot.Empty, source.ReadDisks());
    }

    [Fact]
    public void Disk_counters_are_kept_for_every_volume_and_the_machine_counts_each_whole_disk_once()
    {
        var files = new FakeProcSource()
            .With("/proc/uptime", "100.0 90.0\n")
            .With(
                "/proc/diskstats",
                "   8       0 sda 1 0 10 0 1 0 20 0 0 5 0\n" +
                "   8       1 sda1 1 0 10 0 1 0 20 0 0 5 0\n" +
                "   7       0 loop0 1 0 99 0 1 0 99 0 0 5 0\n")
            .With("/sys/block/sda/stat", "x")
            .With("/sys/block/loop0/stat", "x");

        var disks = Over(files).ReadDisks();

        Assert.Equal(3, disks.Volumes.Count);
        Assert.Equal(10 * 512, disks.Machine!.BytesRead);
        Assert.Equal(20 * 512, disks.Machine.BytesWritten);
    }

    [Fact]
    public void A_folder_is_on_the_mount_that_holds_it_and_that_mounts_device_names_its_counters()
    {
        var files = new FakeProcSource().With("/proc/self/mountinfo", "22 1 8:1 / / rw - ext4 /dev/sda1 rw\n30 22 8:16 / /media rw - ext4 /dev/sdb rw\n");
        var source = Over(files);

        Assert.Equal("/media", source.DriveRootOf("/media/movies"));
        Assert.Equal("8:16", source.VolumeKeyOf("/media"));
        Assert.Equal("/", source.DriveRootOf("/config"));
    }

    [Fact]
    public void A_pending_restart_is_reported_when_the_system_says_so()
    {
        var files = new FakeProcSource().With("/var/run/reboot-required", string.Empty);

        Assert.True(Over(files).ReadRebootPending());
    }

    [Fact]
    public void A_debian_machine_with_no_restart_waiting_says_no()
    {
        var files = new FakeProcSource().With("/var/lib/dpkg/status", "x");

        Assert.False(Over(files).ReadRebootPending());
    }

    [Fact]
    public void A_container_cannot_tell_whether_its_host_needs_a_restart()
    {
        var files = new FakeProcSource().With("/var/lib/dpkg/status", "x").With("/.dockerenv", string.Empty);

        Assert.Null(Over(files).ReadRebootPending());
    }

    [Fact]
    public void A_cpu_limit_with_no_readable_use_falls_back_to_the_host()
    {
        var files = new FakeProcSource()
            .With("/proc/stat", "cpu  100 0 100 700 100 0 0 0 0 0\n")
            .With("/sys/fs/cgroup/cpu.max", "200000 100000\n")
            .With("/sys/fs/cgroup/cpu.stat", "nothing useful\n");

        Assert.Equal(new CpuTimes(800, 1000), Over(files).ReadCpuTimes());
    }

    [Fact]
    public void A_memory_limit_with_no_readable_holding_falls_back_to_the_host()
    {
        var files = new FakeProcSource()
            .With("/proc/meminfo", "MemTotal: 1000 kB\nMemAvailable: 250 kB\n")
            .With("/sys/fs/cgroup/memory.max", "1000000\n")
            .With("/sys/fs/cgroup/memory.current", "unreadable\n");

        Assert.Equal(new MemoryReading(1000 * 1024, 250 * 1024), Over(files).ReadMemory());
    }

    [Fact]
    public void A_path_no_mount_holds_has_no_drive_root_and_no_volume()
    {
        var source = Over(new FakeProcSource().With("/proc/self/mountinfo", string.Empty));

        Assert.Null(source.DriveRootOf("/media/movies"));
        Assert.Null(source.VolumeKeyOf("/media"));
    }
}
