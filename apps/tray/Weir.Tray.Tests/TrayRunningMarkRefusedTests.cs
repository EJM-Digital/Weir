using System.Diagnostics;
using System.Security.AccessControl;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// A mark Windows will not let the account read or write is logged and never thrown: the hooks run inside a time budget, and a
/// missing mark costs Weir a restart after a silent install, where failing the install would cost far more.
/// </summary>
public sealed class TrayRunningMarkRefusedTests : IDisposable
{
    private const string Installed = @"C:\Users\someone\AppData\Local\Weir\current\Weir.exe";

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempRegistryKey _key = TempRegistryKey.Under(@"Software\WeirTrayMarkTests");
    private RefusedRegistryKey? _refusal;

    public void Dispose()
    {
        _refusal?.Dispose();
        _key.Dispose();
        _home.Dispose();
    }

    private string Log => File.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName));

    private TrayRunningMark Mark() => new(_key.Path);

    private static TrayRun Run(int processId) => new(processId, 100, Installed);

    private void Refuse(RegistryRights rights) => _refusal = new RefusedRegistryKey(_key.Path, rights, isVolatile: true);

    [Fact]
    public void A_mark_that_cannot_be_written_is_logged()
    {
        Refuse(RegistryRights.SetValue | RegistryRights.CreateSubKey);

        Mark().Set(Run(1234));

        Assert.Contains("Could not record that Weir is running:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mark_that_cannot_be_read_is_not_cleared_by_an_exiting_tray_and_the_log_says_so()
    {
        Refuse(RegistryRights.QueryValues);

        Mark().Clear(1234);

        Assert.Contains("Could not clear the record that Weir is running:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mark_that_cannot_be_read_is_not_forgotten_by_an_uninstall_and_the_log_says_so()
    {
        Refuse(RegistryRights.QueryValues);

        Mark().Forget(Installed);

        Assert.Contains("Could not clear the record that Weir is running:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mark_that_cannot_be_read_says_nothing_was_left_and_the_log_says_why()
    {
        Refuse(RegistryRights.QueryValues);

        Assert.False(Mark().TakeLeftBy(Installed, _ => false));

        Assert.Contains("Could not read whether Weir was running:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void An_uninstall_with_no_mark_forgets_nothing()
    {
        Mark().Forget(Installed);

        Assert.False(Mark().TakeLeftBy(Installed, _ => false));
    }

    [Fact]
    public void An_install_path_Windows_cannot_make_sense_of_never_matches_a_mark()
    {
        Mark().Set(Run(1234));

        Assert.False(Mark().TakeLeftBy(Installed + "\0", _ => false));
        Assert.True(Mark().TakeLeftBy(Installed, _ => false));
    }

    [Fact]
    public void A_process_Windows_names_no_path_for_is_marked_with_no_path()
    {
        Assert.Equal(string.Empty, TrayRun.ThisProcess(null).Executable);
        Assert.Equal("C:/somewhere/Weir.exe", TrayRun.ThisProcess("C:/somewhere/Weir.exe").Executable);
    }

    [Fact]
    public void A_process_that_has_exited_is_not_running_even_while_something_still_holds_it()
    {
        using var gone = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "whoami.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        gone.WaitForExit();
        var run = new TrayRun(gone.Id, gone.StartTime.ToUniversalTime().Ticks, Installed);

        Assert.False(run.IsRunning(_ => gone));
    }
}
