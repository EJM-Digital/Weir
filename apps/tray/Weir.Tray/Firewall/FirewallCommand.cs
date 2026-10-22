using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Weir.Tray.LanAccess;

namespace Weir.Tray.Firewall;

/// <summary>
/// The command-line entry points for Weir's firewall rule: <c>--configure-firewall</c> and
/// <c>--remove-firewall</c> (run already elevated — <see cref="FirewallElevation"/> is what gets a process there,
/// through Windows' own UAC prompt) and <c>--allow-lan</c> (the documented flag for a program driving Weir
/// unattended, #779). <c>--allow-lan</c> means "reachable on the LAN": it always turns LAN access on, and it
/// creates the firewall rule too when it is already running as administrator. It never elevates itself and never
/// prompts, so a caller with no one to answer a UAC prompt is never handed one, and without administrator rights
/// it succeeds having turned access on and said in the log that the rule is still missing. None of these ever show
/// a window, so <c>--silent</c> changes nothing about them; they are headless by construction.
/// </summary>
static class FirewallCommand
{
    internal const string ConfigureArgument = "--configure-firewall";
    internal const string RemoveArgument = "--remove-firewall";
    internal const string AllowLanArgument = "--allow-lan";

    internal static bool Handles(IEnumerable<string> args) =>
        args.Contains(ConfigureArgument) || args.Contains(RemoveArgument) || args.Contains(AllowLanArgument);

    /// <summary>Runs whichever firewall argument is present. <see cref="Handles"/> must be true first.</summary>
    [ExcludeFromCodeCoverage]
    internal static int Run(IReadOnlyList<string> args, Action<string> log) =>
        Run(args, log, IsElevated, () => new ComFirewallPolicy(), scope => LanAccessSetting.Write(Program.RuntimeHome(), scope));

    /// <summary>
    /// The same behaviour, with elevation checking, the firewall policy itself and where the LAN access choice is
    /// saved substitutable for tests.
    /// </summary>
    internal static int Run(
        IReadOnlyList<string> args,
        Action<string> log,
        Func<bool> isElevated,
        Func<IFirewallPolicy> openPolicy,
        Action<ListenScope> saveLanAccess)
    {
        if (args.Contains(RemoveArgument))
        {
            return RunElevated(log, isElevated, openPolicy, "remove", policy => WeirFirewallRule.Remove(policy));
        }
        if (args.Contains(AllowLanArgument))
        {
            return AllowLan(log, isElevated, openPolicy, saveLanAccess);
        }
        if (args.Contains(ConfigureArgument))
        {
            return RunElevated(log, isElevated, openPolicy, "configure", policy => LogConfigureResult(policy, log));
        }
        throw new InvalidOperationException($"{nameof(FirewallCommand)}.{nameof(Run)} was called without a firewall argument.");
    }

    // A running tray notices the saved choice change and restarts its server to match (LanAccessSync).
    private static int AllowLan(Action<string> log, Func<bool> isElevated, Func<IFirewallPolicy> openPolicy, Action<ListenScope> saveLanAccess)
    {
        try
        {
            saveLanAccess(ListenScope.OtherDevices);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Could not turn LAN access on: {ex.Message}");
            return FirewallExitCode.LanAccessNotSaved;
        }
        log("LAN access is on: Weir listens for other devices on your network.");

        if (!isElevated())
        {
            log("The Weir firewall rule was not created, because this process is not running as administrator. Until it exists, Windows Firewall decides whether other devices get through. Run --allow-lan as administrator, or use \"Allow other devices on your network...\" in the Weir tray menu, to create it.");
            return FirewallExitCode.Success;
        }
        return ApplyToFirewall(log, openPolicy, "configure", policy => LogConfigureResult(policy, log));
    }

    private static int RunElevated(Action<string> log, Func<bool> isElevated, Func<IFirewallPolicy> openPolicy, string verb, Action<IFirewallPolicy> apply)
    {
        if (!isElevated())
        {
            log($"Cannot {verb} the Weir firewall rule: this process is not running as administrator.");
            return FirewallExitCode.NotElevated;
        }
        return ApplyToFirewall(log, openPolicy, verb, apply);
    }

    private static int ApplyToFirewall(Action<string> log, Func<IFirewallPolicy> openPolicy, string verb, Action<IFirewallPolicy> apply)
    {
        try
        {
            apply(openPolicy());
            return FirewallExitCode.Success;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            log($"Could not {verb} the Weir firewall rule: {ex.Message}");
            return FirewallExitCode.FirewallApiError;
        }
    }

    private static void LogConfigureResult(IFirewallPolicy policy, Action<string> log)
    {
        var summary = WeirFirewallRule.Configure(policy, InstallProcesses.Root());
        log($"Firewall rule '{WeirFirewallRule.RuleName}' is in place ({WeirFirewallRule.AllowedProfiles} profiles). Removed {summary.BlockRulesRemoved} block rule(s) for Weir's server.");
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
