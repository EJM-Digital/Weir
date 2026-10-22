using System.Diagnostics;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Setup run over a running Weir, plain or silent, leaves one Weir running. Setup ends the running tray first and the after-install
/// hook arranges one start for after Setup exits. A plain Setup starts Weir itself, so that start finds a tray running from the
/// install and steps aside (no second start, one tray icon); a silent Setup starts nothing, so that start goes ahead and brings Weir
/// back. These run against a throw-away key under HKCU, never the real mark, and a stand-in tray for the one Setup starts.
/// </summary>
public sealed class SetupOverARunningWeirTests : IDisposable
{
    private const int SetupId = 4321;
    private static readonly string[] StartAfterSetup = [Program.NoBrowserArgument, RestartAfterSetup.Argument];

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

    private string InstallFolder => Path.Combine(_root.Path, "Weir", "current");

    private string Tray => Path.Combine(InstallFolder, "Weir.exe");

    // The hook of the new version runs once Setup has ended the running tray; it leaves the mark of a tray that was running.
    private void TheHookRuns()
    {
        var mark = new TrayRunningMark(_key.Path);
        mark.Set(new TrayRun(1234, 100, Tray));
        RestartAfterSetup.Schedule(mark, Tray, _ => false, () => SetupId, _started.Add, _log.Add);
    }

    private bool TheStartAfterSetupStepsAside() =>
        RestartAfterSetup.ShouldStepAside(StartAfterSetup, () => InstallProcesses.AnotherTrayRuns(InstallFolder, _log.Add, "Start after Setup"));

    [Fact]
    public async Task A_plain_Setup_starts_Weir_itself_so_the_start_after_it_steps_aside_and_one_tray_runs()
    {
        TheHookRuns();
        using var startedBySetup = await _servers.StartTrayAsync(InstallFolder);

        var stepsAside = TheStartAfterSetupStepsAside();

        Assert.Single(_started);
        Assert.True(stepsAside);
        Assert.False(startedBySetup.HasExited);
    }

    [Fact]
    public void A_silent_Setup_starts_nothing_so_the_start_after_it_brings_Weir_back()
    {
        TheHookRuns();

        var stepsAside = TheStartAfterSetupStepsAside();

        var helper = Assert.Single(_started);
        Assert.Contains($"Start-Process -FilePath '{Tray}' -ArgumentList '--no-browser','--after-setup'", helper.ArgumentList[^1], StringComparison.Ordinal);
        Assert.False(stepsAside);
    }

    [Fact]
    public async Task A_tray_from_another_install_does_not_make_the_start_after_Setup_step_aside()
    {
        TheHookRuns();
        using var elsewhere = await _servers.StartTrayAsync(Path.Combine(_root.Path, "Elsewhere", "current"));

        Assert.False(TheStartAfterSetupStepsAside());
    }
}
