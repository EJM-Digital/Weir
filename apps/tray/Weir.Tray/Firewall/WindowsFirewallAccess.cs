using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Weir.Tray.Firewall;

/// <summary>The real Windows Firewall, read through COM and written by the same elevated step the tray menu uses.</summary>
sealed class WindowsFirewallAccess(Func<IFirewallPolicy> openPolicy, Func<ProcessStartInfo, Func<int>?> start) : IFirewallAccess
{
    internal WindowsFirewallAccess()
        : this(ComFirewallPolicy.Open, WindowsEffects.StartElevated)
    {
    }

    public bool AllowsWeirIn()
    {
        try
        {
            return WeirFirewallRule.AllowsServerOnCurrentNetwork(openPolicy(), InstallProcesses.Root());
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Unreadable is not the same as missing: asking for a rule nobody can see would only raise a prompt
            // that cannot fix anything.
            TrayLog.Write($"LAN access: Windows Firewall could not be read ({ex.Message}); not asking for a rule.");
            return true;
        }
    }

    public FirewallElevation.Outcome AskToAllow() =>
        FirewallElevation.RunElevated(FirewallCommand.ConfigureArgument, TrayLog.Write, Environment.ProcessPath, start);
}
