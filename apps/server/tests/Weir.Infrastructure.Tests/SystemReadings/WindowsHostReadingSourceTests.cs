using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
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

    /// <summary>A drive letter no drive on this machine has.</summary>
    private static string UnusedDriveRoot() =>
        char.ToString(Enumerable.Range('D', 23).Select(letter => (char)letter).First(letter => !Directory.Exists(letter + @":\"))) + @":\";

    [WindowsFact(OnlyWindows)]
    public void A_registry_that_refuses_leaves_a_restart_unknown_and_the_system_named_by_the_framework()
    {
        var source = new WindowsHostReadingSource(() => throw new UnauthorizedAccessException(), null);

        Assert.Null(source.ReadRebootPending());
        Assert.Equal(RuntimeInformation.OSDescription, source.ReadOperatingSystem());
    }

    [WindowsFact(OnlyWindows)]
    public void A_registry_without_the_keys_means_no_restart_is_pending_and_the_framework_names_the_system()
    {
        var source = new WindowsHostReadingSource(() => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64), null);

        Assert.False(source.ReadRebootPending());
        Assert.Equal(RuntimeInformation.OSDescription, source.ReadOperatingSystem());
    }

    [WindowsTheory(OnlyWindows)]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", null, true)]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", null, true)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations", true)]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Session Manager", "SomethingElse", false)]
    public void A_restart_is_pending_when_any_of_the_three_places_windows_records_one_says_so(string key, string? value, bool pending)
    {
        var root = @"Software\WeirTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var made = Registry.CurrentUser.CreateSubKey(root + @"\" + key))
            {
                if (value is not null)
                {
                    made.SetValue(value, new[] { "move this" });
                }
            }

            var source = new WindowsHostReadingSource(() => Registry.CurrentUser.OpenSubKey(root)!, null);

            Assert.Equal(pending, source.ReadRebootPending());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\WeirTests", throwOnMissingSubKey: false);
        }
    }

    [WindowsFact(OnlyWindows)]
    public void Only_a_fixed_drive_whose_counters_can_be_read_is_listed()
    {
        var fixedDrive = new DriveInfo(TempRoot);
        var absent = new DriveInfo(UnusedDriveRoot());

        var listed = new WindowsHostReadingSource(null, () => [fixedDrive, absent], _ => new DiskCounters(1, 2, 3, 4)).ReadDisks();
        var refused = new WindowsHostReadingSource(null, () => [fixedDrive, absent], _ => null).ReadDisks();

        Assert.Single(listed.Volumes);
        Assert.Empty(refused.Volumes);
    }

    [WindowsFact(OnlyWindows)]
    public void A_list_of_drives_that_cannot_be_read_is_no_counters_at_all()
    {
        var source = new WindowsHostReadingSource(null, () => throw new IOException("The list is not there."));

        Assert.Same(DiskSnapshot.Empty, source.ReadDisks());
    }

    [WindowsFact(OnlyWindows)]
    public void A_kernel_call_the_system_does_not_have_is_no_reading()
    {
        Assert.Null(WindowsHostReadingSource.Guarded<int>(() => throw new DllNotFoundException()));
        Assert.Null(WindowsHostReadingSource.Guarded<int>(() => throw new EntryPointNotFoundException()));
        Assert.Equal(7, WindowsHostReadingSource.Guarded<int>(() => 7));
    }

    [Theory]
    [InlineData("Windows 10 Pro", "19045", "Windows 10 Pro")]
    [InlineData("Windows 10 Pro", "22000", "Windows 11 Pro")]
    [InlineData("Windows 10 Pro", "26300", "Windows 11 Pro")]
    [InlineData("Windows 10 Pro", "not a number", "Windows 10 Pro")]
    [InlineData("Windows 10 Pro", null, "Windows 10 Pro")]
    [InlineData("Windows Server 2025 Datacenter", "26100", "Windows Server 2025 Datacenter")]
    [InlineData("", "26100", null)]
    [InlineData(null, "26100", null)]
    public void The_product_name_is_the_registrys_with_Windows_11_called_that_from_its_first_build(string? product, string? build, string? expected)
    {
        Assert.Equal(expected, WindowsHostReadingSource.NameOf(product, build));
    }

    [Fact]
    public void A_registry_value_that_is_not_a_text_names_no_product()
    {
        Assert.Null(WindowsHostReadingSource.NameOf(7, "26100"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a rename", true)]
    [InlineData(7, false)]
    public void A_registry_value_lists_something_when_it_is_a_text_or_a_list_with_entries(object? value, bool expected)
    {
        Assert.Equal(expected, WindowsHostReadingSource.HasEntries(value));
    }

    [Fact]
    public void A_list_of_renames_with_entries_is_pending_and_an_empty_list_is_not()
    {
        Assert.True(WindowsHostReadingSource.HasEntries(new[] { @"\??\C:\a", string.Empty }));
        Assert.False(WindowsHostReadingSource.HasEntries(Array.Empty<string>()));
    }

    [WindowsFact(OnlyWindows)]
    public void A_drive_that_is_not_a_fixed_disk_or_not_a_drive_has_no_volume_key()
    {
        Assert.Null(_source.VolumeKeyOf(UnusedDriveRoot()));
        Assert.Null(_source.VolumeKeyOf(@"1:\"));
    }

    [WindowsFact(OnlyWindows)]
    public void Counters_and_space_are_not_asked_of_something_that_is_not_a_drive_or_is_not_there()
    {
        Assert.Null(WindowsNative.ReadVolumeCounters(string.Empty));
        Assert.Null(WindowsNative.ReadVolumeCounters(@"\\nas\media\"));
        Assert.Null(WindowsNative.ReadVolumeCounters(UnusedDriveRoot()));
        Assert.Null(WindowsNative.ReadSpace(UnusedDriveRoot()));
    }
}
