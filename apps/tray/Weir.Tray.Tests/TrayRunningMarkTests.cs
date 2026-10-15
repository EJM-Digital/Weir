using Microsoft.Win32;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The mark that tells the after-install hook Weir was running when Setup stopped it, and only for the install whose
/// Weir.exe set it. These run against a throw-away key under HKCU, never the real mark.
/// </summary>
public sealed class TrayRunningMarkTests : IDisposable
{
    private const string Installed = @"C:\Users\someone\AppData\Local\Weir\current\Weir.exe";

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempRegistryKey _key = TempRegistryKey.Under(@"Software\WeirTrayMarkTests");

    public void Dispose()
    {
        _key.Dispose();
        _home.Dispose();
    }

    private TrayRunningMark Mark() => new(_key.Path);

    private static TrayRun Run(int processId, string executable = Installed) => new(processId, 100, executable);

    private bool Take(string executable = Installed, bool stillRunning = false) =>
        Mark().TakeLeftBy(executable, _ => stillRunning);

    [Fact]
    public void Nothing_is_marked_before_a_tray_starts()
    {
        Assert.False(Take());
    }

    [Fact]
    public void A_tray_that_was_ended_without_an_orderly_exit_is_found_by_its_install()
    {
        Mark().Set(Run(1234));

        Assert.True(Take());
    }

    [Fact]
    public void Reading_the_mark_removes_it()
    {
        Mark().Set(Run(1234));

        Take();

        Assert.False(Take());
    }

    [Theory]
    [InlineData(@"c:\users\SOMEONE\appdata\local\weir\CURRENT\weir.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Weir\other\..\current\Weir.exe")]
    public void The_install_is_matched_by_its_normalised_path_without_regard_to_case(string executable)
    {
        Mark().Set(Run(1234, executable));

        Assert.True(Take());
    }

    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\Weir-portable\Weir.exe")]
    [InlineData(@"D:\a\Weir\Weir\dist\windows\pack\Weir.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Weir\current\server\WeirServer.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Weir-old\current\Weir.exe")]
    public void A_tray_run_from_anywhere_but_the_install_never_counts_and_its_mark_is_left_alone(string executable)
    {
        Mark().Set(Run(1234, executable));

        Assert.False(Take());
        Assert.True(Take(executable));
    }

    [Fact]
    public void A_mark_that_names_no_executable_never_counts()
    {
        Mark().Set(Run(1234, executable: ""));

        Assert.False(Take());
        Assert.False(Take(executable: ""));
    }

    [Fact]
    public void A_tray_that_is_still_running_is_not_one_that_was_ended()
    {
        Mark().Set(Run(1234));

        Assert.False(Take(stillRunning: true));
        Assert.True(Take());
    }

    [Fact]
    public void A_tray_that_exits_in_order_clears_its_mark()
    {
        Mark().Set(Run(1234));

        Mark().Clear(1234);

        Assert.False(Take());
    }

    [Fact]
    public void A_tray_that_exits_does_not_clear_the_mark_of_one_started_since()
    {
        Mark().Set(Run(1234));
        Mark().Set(Run(5678));

        Mark().Clear(1234);

        Assert.True(Take());
    }

    [Fact]
    public void Clearing_a_mark_that_is_not_there_is_harmless()
    {
        Mark().Clear(1234);

        Assert.False(Take());
    }

    [Fact]
    public void An_uninstall_forgets_the_installs_mark_so_the_next_install_is_a_first_one()
    {
        Mark().Set(Run(1234));

        Mark().Forget(Installed);

        Assert.False(Take());
    }

    [Fact]
    public void An_uninstall_leaves_the_mark_of_a_tray_run_from_elsewhere()
    {
        const string Portable = @"C:\Users\someone\Downloads\Weir-portable\Weir.exe";
        Mark().Set(Run(1234, Portable));

        Mark().Forget(Installed);

        Assert.True(Take(Portable));
    }

    [Fact]
    public void This_process_is_running_and_a_process_with_its_id_started_at_another_time_is_not()
    {
        var self = TrayRun.ThisProcess();

        Assert.True(self.IsRunning());
        Assert.False((self with { StartedAtTicks = self.StartedAtTicks + 1 }).IsRunning());
    }

    [Fact]
    public void A_process_that_does_not_exist_is_not_running()
    {
        Assert.False(Run(int.MaxValue).IsRunning());
    }

    [Fact]
    public void The_mark_does_not_survive_a_restart_or_a_sign_out()
    {
        Mark().Set(Run(1234));

        using var key = Registry.CurrentUser.OpenSubKey(_key.Path, writable: true)!;

        // Windows keeps a volatile key in memory only, so it is gone with the user's session.
        Assert.True(IsVolatile(key));
    }

    // .NET has no property for a key's volatility, but Windows refuses a stable subkey under a volatile one.
    private static bool IsVolatile(RegistryKey key)
    {
        try
        {
            key.CreateSubKey("stable", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None).Dispose();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
