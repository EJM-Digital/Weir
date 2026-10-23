using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Weir.Infrastructure.Runtime;

/// <summary>An inbound firewall rule for Weir's own server program, as much of it as the decision needs.</summary>
internal readonly record struct ServerFirewallRule(bool IsAllow, bool Enabled, int Profiles);

/// <summary>
/// Whether Windows Firewall lets other devices reach Weir's server, from the rules for its program and the network
/// this PC is on. Pure, so the Windows-only COM read stays out of it.
/// </summary>
internal static class NetworkAccessDecision
{
    /// <summary>
    /// Other devices get in when an enabled allow rule covers a network this PC is on and no enabled block rule
    /// does: a block wins, as in Windows Firewall itself. Weir's own "Weir" rule and one Windows made when someone
    /// clicked Allow on its prompt count the same.
    /// </summary>
    internal static FirewallVerdict Judge(IReadOnlyList<ServerFirewallRule> rules, int currentProfiles)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var enabled = rules.Where(rule => rule.Enabled && (rule.Profiles & currentProfiles) != 0).ToList();
        var allowed = enabled.Any(rule => rule.IsAllow) && !enabled.Any(rule => !rule.IsAllow);
        return allowed ? FirewallVerdict.Allows : FirewallVerdict.Blocks;
    }
}

/// <summary>
/// The Windows package's read of Weir's own firewall rules, through the same COM policy object the tray writes to
/// (<c>HNetCfg.FwPolicy2</c> / <c>INetFwPolicy2</c> — see <c>apps/tray/Weir.Tray/Firewall/ComFirewallPolicy.cs</c>).
/// Reading a rule and the active network profile needs no administrator rights.
///
/// This does not share code with the tray's writer: <c>apps/server</c> and <c>apps/tray</c> are separate
/// solutions with no shared project reference, and only the tray ever creates or removes the rule. Keep the
/// program path in step with <c>WeirFirewallRule</c> there if it changes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ComServerFirewall : IServerFirewall
{
    private const string ServerExeName = "WeirServer.exe";

    // NET_FW_RULE_DIRECTION_IN.
    private const int DirectionIn = 1;

    // NET_FW_ACTION_.
    private const int ActionAllow = 1;

    private readonly Func<FirewallVerdict> _read;

    public ComServerFirewall()
        : this(Read)
    {
    }

    /// <summary>A test passes a read that fails the way Windows Firewall's policy object can.</summary>
    internal ComServerFirewall(Func<FirewallVerdict> read) => _read = read;

    public FirewallVerdict Judge()
    {
        try
        {
            return _read();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The COM policy object could not be reached (a locked-down machine, a firewall service that is not
            // running): the firewall's answer is unknown, so this reports what Weir can prove, that no allow rule
            // was seen.
            return FirewallVerdict.Blocks;
        }
    }

    [ExcludeFromCodeCoverage]
    private static FirewallVerdict Read()
    {
        var programPath = Path.Combine(AppContext.BaseDirectory, ServerExeName);
        dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
        var currentProfiles = (int)policy.CurrentProfileTypes;

        var rules = new List<ServerFirewallRule>();
        foreach (dynamic comRule in policy.Rules)
        {
            if (TryRead(comRule, programPath) is { } rule)
            {
                rules.Add(rule);
            }
        }
        return NetworkAccessDecision.Judge(rules, currentProfiles);
    }

    // Only inbound rules for Weir's own server exe are meaningful here; everything else reads as null.
    internal static ServerFirewallRule? TryRead(dynamic comRule, string programPath)
    {
        try
        {
            if ((int)comRule.Direction != DirectionIn)
            {
                return null;
            }
            string? applicationName = comRule.ApplicationName;
            if (!string.Equals(applicationName, programPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return new ServerFirewallRule((int)comRule.Action == ActionAllow, (bool)comRule.Enabled, (int)comRule.Profiles);
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal static dynamic CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"Windows Firewall's COM object '{progId}' is not registered on this machine.");
        // A registered COM class is a reference type, which always yields an instance.
        return Activator.CreateInstance(type)!;
    }
}
