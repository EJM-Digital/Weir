using System.Diagnostics;
using System.Runtime.InteropServices;
using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests.Firewall;

/// <summary>
/// How the tray reaches Windows Firewall: the uninstall hook's removal, the access the tray menu and the first-run question use,
/// and the elevated step, with the firewall and the Windows admin prompt replaced by stand-ins that note what they were asked.
/// </summary>
public sealed class FirewallAccessTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    private string Log => File.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName));

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Uninstalling_without_administrator_rights_leaves_the_rule_and_says_so()
    {
        var opened = false;

        FirewallInstallHooks.RemoveRuleIfElevated(() => false, () =>
        {
            opened = true;
            return new FakeFirewallPolicy();
        });

        Assert.False(opened);
        Assert.Contains("leaving the Weir firewall rule in place", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstalling_as_an_administrator_removes_the_rule()
    {
        var policy = new FakeFirewallPolicy(WeirFirewallRule.DesiredRule(@"C:\Weir"));

        FirewallInstallHooks.RemoveRuleIfElevated(() => true, () => policy);

        Assert.DoesNotContain(policy.Rules, rule => rule.Name == WeirFirewallRule.RuleName);
        Assert.Contains("removed the Weir firewall rule", Log, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(FirewallFailures))]
    public void A_firewall_that_cannot_be_changed_does_not_stop_the_uninstall(Func<Exception> failure)
    {
        FirewallInstallHooks.RemoveRuleIfElevated(() => true, () => throw failure());

        Assert.Contains("could not remove the Weir firewall rule", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_run_question_is_not_asked_of_a_silent_start()
    {
        FirewallInstallHooks.AskOnFirstRun(["--silent"]);

        Assert.Contains("First-run firewall prompt skipped", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_Firewall_that_cannot_be_read_is_not_asked_for_a_rule()
    {
        var access = new WindowsFirewallAccess(() => throw new InvalidOperationException("The firewall service is stopped."), _ => null);

        Assert.True(access.AllowsWeirIn());
        Assert.Contains("not asking for a rule", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_Firewall_without_the_rule_does_not_let_Weir_in()
    {
        var access = new WindowsFirewallAccess(() => new FakeFirewallPolicy(), _ => null);

        Assert.False(access.AllowsWeirIn());
    }

    [Fact]
    public void Asking_to_allow_runs_this_program_elevated_with_the_configure_argument()
    {
        ProcessStartInfo? started = null;
        var access = new WindowsFirewallAccess(() => new FakeFirewallPolicy(), info =>
        {
            started = info;
            return () => FirewallExitCode.Success;
        });

        Assert.Equal(FirewallElevation.Outcome.Configured, access.AskToAllow());

        Assert.Equal(FirewallCommand.ConfigureArgument, started!.Arguments);
        Assert.Equal("runas", started.Verb);
    }

    [Fact]
    public void Nothing_waits_for_a_program_that_did_not_start_and_the_exit_code_of_one_that_did_is_given()
    {
        Assert.Null(FirewallElevation.WaitFor(null));

        var wait = FirewallElevation.WaitFor(Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 3") { UseShellExecute = false, CreateNoWindow = true }));

        Assert.Equal(3, wait!());
    }

    [Fact]
    public void A_firewall_command_without_a_firewall_argument_is_a_mistake()
    {
        Assert.Throws<InvalidOperationException>(() => FirewallCommand.Run(["--silent"], _ => { }, () => true, () => new FakeFirewallPolicy(), _ => { }));
    }

    [Fact]
    public void Whether_the_tray_runs_elevated_is_answered_from_its_own_Windows_account()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var expected = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        Assert.Equal(expected, FirewallCommand.IsElevated());
    }

    [Fact]
    public async Task The_rule_is_not_widened_for_a_PC_that_listens_for_itself_only()
    {
        await FirewallRuleWidening.AskInBackground(_home.Path, ListenScope.ThisPcOnly, interactive: true);
        WindowsStartup.WidenFirewallRule(_home.Path, ListenScope.ThisPcOnly, interactive: false);

        Assert.False(File.Exists(Path.Combine(_home.Path, FirewallRuleWidening.MarkerFileName)));
    }

    public static TheoryData<Func<Exception>> FirewallFailures() =>
        new()
        {
            () => new InvalidOperationException("The firewall service is stopped."),
            () => new UnauthorizedAccessException("Access is denied."),
            ComFailure,
        };

#pragma warning disable CA2201 // A COM call that fails throws COMException; the stand-in has to throw the same.
    private static Exception ComFailure() => new COMException("The firewall is not available.");
#pragma warning restore CA2201
}
