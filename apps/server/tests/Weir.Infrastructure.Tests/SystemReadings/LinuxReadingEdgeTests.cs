using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.SystemReadings;

namespace Weir.Infrastructure.Tests.SystemReadings;

/// <summary>What the Linux readings make of files that are short, empty, unreadable or say "no limit".</summary>
public sealed class LinuxReadingEdgeTests
{
    [Fact]
    public void A_cpu_line_too_short_to_count_gives_no_times_and_one_without_iowait_counts_idle_alone()
    {
        Assert.Null(LinuxParsers.ParseCpuTimes("cpu  1 2 3\n"));
        Assert.Equal(new CpuTimes(Idle: 4, Total: 10), LinuxParsers.ParseCpuTimes("cpu  1 2 3 4\n"));
    }

    [Theory]
    [InlineData("MemTotal: 0 kB\nMemAvailable: 5 kB\n")]
    [InlineData("MemTotal: 100 kB\n")]
    [InlineData("MemTotal: many kB\nMemAvailable: 5 kB\n")]
    [InlineData("MemTotal:\nMemAvailable: 5 kB\n")]
    [InlineData(null)]
    public void Memory_that_is_not_fully_described_gives_no_reading(string? text) =>
        Assert.Null(LinuxParsers.ParseMemory(text));

    [Theory]
    [InlineData("12.9 3.0", 12L)]
    [InlineData("abc 3.0", null)]
    [InlineData("-5 3.0", null)]
    [InlineData(null, null)]
    public void Uptime_is_the_first_number_when_it_is_not_negative(string? text, long? expected) =>
        Assert.Equal(expected, LinuxParsers.ParseUptimeSeconds(text));

    [Theory]
    [InlineData("PRETTY_NAME=\"Debian GNU/Linux 12\"\n", "Debian GNU/Linux 12")]
    [InlineData("PRETTY_NAME=\"\"\n", null)]
    [InlineData("NAME=Debian\n", null)]
    [InlineData(null, null)]
    public void The_operating_system_is_its_pretty_name_when_it_has_one(string? text, string? expected) =>
        Assert.Equal(expected, LinuxParsers.ParseOsPrettyName(text));

    [Theory]
    [InlineData(null, "/data")]
    [InlineData("   ", "/data")]
    [InlineData("1 2 8:1 / /data rw - ext4 /dev/sda1 rw\n", "")]
    [InlineData("1 2 8:1 / /data rw - ext4 /dev/sda1 rw\n", "   ")]
    public void No_mount_is_found_without_a_mount_table_or_a_path(string? mountInfo, string path) =>
        Assert.Null(LinuxParsers.MountOf(mountInfo, path));

    [Theory]
    [InlineData("max 100000", null)]
    [InlineData("200000", null)]
    [InlineData("lots 100000", null)]
    [InlineData("200000 whenever", null)]
    [InlineData("0 100000", null)]
    [InlineData("200000 0", null)]
    [InlineData("200000 100000", 2d)]
    [InlineData(null, null)]
    public void The_cores_a_container_is_given_come_from_its_quota_and_period(string? text, double? expected) =>
        Assert.Equal(expected, LinuxParsers.ParseCpuMaxCores(text));

    [Theory]
    [InlineData("max", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("plenty", null)]
    [InlineData("42\n", 42L)]
    public void A_byte_count_is_a_positive_number_and_a_limit_of_max_is_none(string? text, long? expected) =>
        Assert.Equal(expected, LinuxParsers.ParseBytes(text));

    [Theory]
    [InlineData("usage_usec 5\nuser_usec 2\n", 5L)]
    [InlineData("usage_usec lots\n", null)]
    [InlineData("usage_usec 5 6\n", null)]
    [InlineData("user_usec 2\n", null)]
    [InlineData(null, null)]
    public void The_cgroups_processor_time_is_its_usage_line(string? text, long? expected) =>
        Assert.Equal(expected, LinuxParsers.ParseCpuUsageMicroseconds(text));

    [Fact]
    public void A_mount_table_line_with_too_few_columns_is_skipped()
    {
        var mount = LinuxParsers.MountOf("garbage\n1 2 8:1 / /data rw - ext4 /dev/sda1 rw\n", "/data/films");

        Assert.Equal(new Mount("/data", "8:1"), mount);
    }

    [Fact]
    public void A_machine_with_no_container_or_package_marks_does_not_say_whether_a_reboot_is_pending()
    {
        var debian = new FakeProcSource().With("/var/lib/dpkg/status", "x");
        var container = new FakeProcSource().With("/var/lib/dpkg/status", "x").With("/.dockerenv", "x");
        var pending = new FakeProcSource().With("/var/run/reboot-required", "x");

        Assert.False(new LinuxHostReadingSource(debian).ReadRebootPending());
        Assert.Null(new LinuxHostReadingSource(container).ReadRebootPending());
        Assert.Null(new LinuxHostReadingSource(new FakeProcSource()).ReadRebootPending());
        Assert.True(new LinuxHostReadingSource(pending).ReadRebootPending());
    }

    [Fact]
    public void Disks_are_counted_even_with_no_uptime_and_without_a_whole_disk_among_them()
    {
        var files = new FakeProcSource()
            .With("/proc/diskstats", "   7       0 loop0 1 0 8 0 1 0 8 0 0 5 0 0 0 0 0 0 0\n");

        var snapshot = new LinuxHostReadingSource(files, new FakeTimeProvider()).ReadDisks();

        Assert.Single(snapshot.Volumes);
        Assert.Null(snapshot.Machine);
    }

    [Fact]
    public void A_drive_root_that_is_not_a_path_has_no_space_to_read()
    {
        Assert.Null(new LinuxHostReadingSource(new FakeProcSource()).ReadSpace(string.Empty));
    }

    [Fact]
    public void The_machines_own_files_are_read_when_none_are_given_and_a_missing_one_reads_as_nothing()
    {
        var real = new ProcSource();
        var missing = Path.Join(Path.GetTempPath(), "weir-no-such-file-" + Guid.NewGuid().ToString("N"));

        Assert.Null(real.ReadAllText(missing));
        Assert.False(real.Exists(missing));
        Assert.True(real.Exists(Path.GetTempPath()));
        Assert.NotNull(new LinuxHostReadingSource().ReadSpace(Path.GetTempPath()));
    }
}
