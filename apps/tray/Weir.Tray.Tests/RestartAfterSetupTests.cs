using System.Diagnostics;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// A silent Setup over a running Weir stops it, and Setup starts nothing afterwards (#942). Weir starts again when it was
/// running, stays stopped when it was not, and a plain Setup, which starts Weir itself, is not started a second time.
/// These run against a throw-away key under HKCU, never the real mark.
/// </summary>
public sealed class RestartAfterSetupTests : IDisposable
{
    private const string Executable = @"C:\Users\someone\AppData\Local\Weir\current\Weir.exe";
    private const int SetupId = 4321;

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempDirectory _root = TempDirectory.Create();
    private readonly StandInServers _servers = new();
    private readonly TempRegistryKey _key = TempRegistryKey.Under(@"Software\WeirTrayMarkTests");
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<string> _log = [];

    public void Dispose()
    {
        _key.Dispose();
        _servers.Dispose();
        _root.Dispose();
        _home.Dispose();
    }

    private TrayRunningMark Mark() => new(_key.Path);

    private static TrayRun Run(int processId, string executable = Executable) => new(processId, 100, executable);

    private void Schedule(Func<int?>? setupProcessId = null, bool stillRunning = false) =>
        RestartAfterSetup.Schedule(Mark(), Executable, _ => stillRunning, setupProcessId ?? (() => SetupId), _started.Add, _log.Add);

    [Fact]
    public void A_tray_that_was_running_is_started_again_once_Setup_has_exited()
    {
        Mark().Set(Run(1234));

        Schedule();

        var helper = Assert.Single(_started);
        var script = helper.ArgumentList[^1];
        Assert.Contains($"Get-Process -Id {SetupId}", script, StringComparison.Ordinal);
        Assert.Contains($"Start-Process -FilePath '{Executable}' -ArgumentList '--no-browser','--after-setup'", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("WaitForExit", StringComparison.Ordinal) < script.IndexOf("Start-Process", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tray_that_was_not_running_is_left_stopped()
    {
        Schedule();

        Assert.Empty(_started);
        Assert.Contains(_log, line => line.Contains("left stopped"));
    }

    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\Weir-portable\Weir.exe")]
    [InlineData(@"D:\a\Weir\Weir\dist\windows\pack\Weir.exe")]
    public void A_tray_run_from_somewhere_other_than_the_install_starts_nothing(string executable)
    {
        Mark().Set(Run(1234, executable));

        Schedule();

        Assert.Empty(_started);
    }

    [Fact]
    public void A_first_install_starts_nothing_even_when_an_earlier_install_left_a_mark()
    {
        Mark().Set(Run(1234));
        Mark().Forget(Executable);

        Schedule();

        Assert.Empty(_started);
    }

    [Fact]
    public void A_tray_that_is_still_running_is_not_started_again()
    {
        Mark().Set(Run(1234));

        Schedule(stillRunning: true);

        Assert.Empty(_started);
    }

    [Fact]
    public void A_tray_that_quit_in_order_is_left_stopped()
    {
        var mark = Mark();
        mark.Set(Run(1234));
        mark.Clear(1234);

        Schedule();

        Assert.Empty(_started);
    }

    [Fact]
    public void One_install_restarts_Weir_once()
    {
        Mark().Set(Run(1234));

        Schedule();
        Schedule();

        Assert.Single(_started);
    }

    [Fact]
    public void A_tray_that_cannot_find_Setup_says_so_and_starts_nothing()
    {
        Mark().Set(Run(1234));

        Schedule(setupProcessId: () => null);

        Assert.Empty(_started);
        Assert.Contains(_log, line => line.Contains("Setup could not be identified"));
    }

    [Fact]
    public void A_start_that_fails_is_logged_and_does_not_fail_the_install()
    {
        Mark().Set(Run(1234));

        RestartAfterSetup.Schedule(Mark(), Executable, _ => false, () => SetupId, _ => throw new InvalidOperationException("no PowerShell"), _log.Add);

        Assert.Contains(_log, line => line.Contains("could not be started again") && line.Contains("no PowerShell"));
    }

    [Fact]
    public void The_helper_runs_PowerShell_from_System32_without_a_window_or_inherited_handles()
    {
        var helper = RestartAfterSetup.HelperStart(SetupId, Executable);

        Assert.StartsWith(Environment.SystemDirectory, helper.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(helper.FileName));
        Assert.Equal(ProcessWindowStyle.Hidden, helper.WindowStyle);
        Assert.True(helper.UseShellExecute, "a start through the shell hands the helper none of Setup's pipe handles");
    }

    [Fact]
    public void An_apostrophe_in_the_path_stays_inside_the_quoted_path()
    {
        var script = RestartAfterSetup.HelperScript(SetupId, @"C:\Users\O'Brien\AppData\Local\Weir\current\Weir.exe");

        Assert.Contains(@"-FilePath 'C:\Users\O''Brien\AppData\Local\Weir\current\Weir.exe'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_after_Setup_steps_aside_for_a_tray_that_is_already_running()
    {
        Assert.True(RestartAfterSetup.ShouldStepAside(["--no-browser", "--after-setup"], () => true));
    }

    [Fact]
    public void A_start_after_Setup_goes_ahead_when_no_tray_is_running()
    {
        Assert.False(RestartAfterSetup.ShouldStepAside(["--no-browser", "--after-setup"], () => false));
    }

    [Fact]
    public void Any_other_start_never_steps_aside_or_looks_for_a_tray()
    {
        var asked = false;

        var stepsAside = RestartAfterSetup.ShouldStepAside(["--no-browser"], () => asked = true);

        Assert.False(stepsAside);
        Assert.False(asked);
    }

    [Fact]
    public async Task A_tray_running_from_this_install_is_found_and_one_from_another_install_is_not()
    {
        var install = Path.Combine(_root.Path, "install", "current");
        var elsewhere = Path.Combine(_root.Path, "Deluno", "Weir", "app", "current");
        await _servers.StartTrayAsync(elsewhere);

        Assert.False(InstallProcesses.AnotherTrayRuns(install, _log.Add, "test"));

        await _servers.StartTrayAsync(install);

        Assert.True(InstallProcesses.AnotherTrayRuns(install, _log.Add, "test"));
    }

    [Fact]
    public void Setup_is_the_process_that_started_the_hook()
    {
        var parent = ParentProcess.Id();

        Assert.NotNull(parent);
        using var process = Process.GetProcessById(parent.Value);
        Assert.False(process.HasExited);
    }
}
