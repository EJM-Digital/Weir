using System.ComponentModel;
using System.Diagnostics;
using Weir.Tray.Firewall;
using Xunit;

namespace Weir.Tray.Tests.Firewall;

/// <summary>
/// How the elevation helper's end is read: the elevated Weir.exe's exit code is Configured, the Windows consent prompt's own "No" is
/// Declined (the person's choice, not a failure), and everything else is Failed with the reason in the log.
/// </summary>
public sealed class FirewallElevationTests
{
    private const int CancelledByUser = 1223;
    private const string Exe = @"C:\Users\someone\AppData\Local\Weir\current\Weir.exe";

    private readonly List<string> _log = [];
    private readonly List<ProcessStartInfo> _started = [];

    private FirewallElevation.Outcome Run(Func<ProcessStartInfo, Func<int>?> start, string? exe = Exe, string argument = FirewallCommand.ConfigureArgument)
    {
        return FirewallElevation.RunElevated(argument, _log.Add, exe, info =>
        {
            _started.Add(info);
            return start(info);
        });
    }

    [Fact]
    public void An_elevated_step_that_exits_with_success_is_configured()
    {
        Assert.Equal(FirewallElevation.Outcome.Configured, Run(_ => () => FirewallExitCode.Success));
        Assert.Empty(_log);
    }

    [Fact]
    public void The_helper_is_this_same_weir_exe_started_with_the_runas_verb_and_the_argument()
    {
        Run(_ => () => FirewallExitCode.Success, argument: FirewallCommand.RemoveArgument);

        var info = Assert.Single(_started);
        Assert.Equal(Exe, info.FileName);
        Assert.Equal(FirewallCommand.RemoveArgument, info.Arguments);
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
    }

    [Theory]
    [InlineData(FirewallExitCode.NotElevated)]
    [InlineData(FirewallExitCode.FirewallApiError)]
    [InlineData(42)]
    public void An_elevated_step_that_exits_with_any_other_code_failed_and_the_log_says_which(int exitCode)
    {
        Assert.Equal(FirewallElevation.Outcome.Failed, Run(_ => () => exitCode));
        Assert.Equal([$"The elevated firewall step exited with code {exitCode}."], _log);
    }

    [Fact]
    public void The_consent_prompt_answered_no_is_declined_and_not_a_failure()
    {
        var outcome = Run(_ => throw new Win32Exception(CancelledByUser));

        Assert.Equal(FirewallElevation.Outcome.Declined, outcome);
        Assert.Equal(["The Windows admin prompt to allow Weir on the network was declined."], _log);
    }

    [Fact]
    public void Any_other_reason_the_step_could_not_start_is_a_failure_with_the_windows_message()
    {
        var outcome = Run(_ => throw new Win32Exception(2, "The system cannot find the file specified."));

        Assert.Equal(FirewallElevation.Outcome.Failed, outcome);
        Assert.Equal(["Could not start the elevated firewall step: The system cannot find the file specified."], _log);
    }

    [Fact]
    public void A_step_that_started_no_process_failed()
    {
        Assert.Equal(FirewallElevation.Outcome.Failed, Run(_ => null));
        Assert.Equal(["The elevated firewall step did not start."], _log);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_path_to_this_weir_exe_nothing_is_started(string? exe)
    {
        Assert.Equal(FirewallElevation.Outcome.Failed, Run(_ => () => FirewallExitCode.Success, exe));
        Assert.Empty(_started);
        Assert.Equal(["Could not find Weir's own executable path to relaunch elevated for the firewall step."], _log);
    }
}
