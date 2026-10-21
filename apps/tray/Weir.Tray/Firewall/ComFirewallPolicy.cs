using System.Runtime.InteropServices;

namespace Weir.Tray.Firewall;

/// <summary>
/// The real Windows Firewall, through its COM policy object (<c>HNetCfg.FwPolicy2</c> / <c>INetFwPolicy2</c>).
///
/// COM is used instead of shelling out to <c>netsh advfirewall</c>: reading every rule needs no process
/// launch or text parsing, writing a rule is one call instead of assembling and re-parsing a command line, and a failure comes back as an HRESULT .NET turns into a <see cref="COMException"/>
/// rather than an exit code and stderr text to interpret. No NetFwTypeLib interop assembly is referenced, so every
/// member is late-bound through <c>dynamic</c>, including enumerating <c>Rules</c>: the C# compiler resolves
/// <c>foreach</c> over a <c>dynamic</c> COM collection through its <c>_NewEnum</c>/<c>IEnumVARIANT</c> enumerator,
/// the same mechanism classic Office automation relies on.
/// </summary>
sealed class ComFirewallPolicy : IFirewallPolicy
{
    private const string PolicyProgId = "HNetCfg.FwPolicy2";
    private const string RuleProgId = "HNetCfg.FWRule";
    private const string RuleGroup = "Weir";
    private const string RuleDescription = "Lets other devices on your network reach Weir. Managed by Weir; remove it by uninstalling Weir or using its tray menu.";

    // NET_FW_IP_PROTOCOL_TCP.
    private const int ProtocolTcp = 6;

    // NET_FW_RULE_DIRECTION_.
    private const int DirectionIn = 1;
    private const int DirectionOut = 2;

    // NET_FW_ACTION_.
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;

    private readonly dynamic _policy;
    private readonly Func<string, object> _create;

    internal ComFirewallPolicy()
        : this(CreateComObject(PolicyProgId), CreateComObject)
    {
    }

    /// <summary>The real firewall's rules, for the code that takes them as <see cref="IFirewallPolicy"/>.</summary>
    internal static IFirewallPolicy Open() => new ComFirewallPolicy();

    /// <param name="policy">The firewall's policy object (<c>INetFwPolicy2</c>).</param>
    /// <param name="create">Makes a COM object from its ProgID; a new rule is made through it.</param>
    internal ComFirewallPolicy(object policy, Func<string, object> create)
    {
        _policy = policy;
        _create = create;
    }

    public IReadOnlyList<FirewallRule> Rules
    {
        get
        {
            var rules = new List<FirewallRule>();
            foreach (dynamic rule in _policy.Rules)
            {
                if (ToFirewallRule(rule) is { } mapped)
                {
                    rules.Add(mapped);
                }
            }
            return rules;
        }
    }

    public FirewallProfiles CurrentProfiles => (FirewallProfiles)(int)_policy.CurrentProfileTypes;

    public void AddOrUpdateRule(FirewallRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // The COM API has no "update"; an existing rule with this name is removed first so there is
        // never more than one Weir rule, whatever the previous call left behind.
        RemoveRule(rule.Name);

        dynamic comRule = _create(RuleProgId);
        comRule.Name = rule.Name;
        comRule.Description = RuleDescription;
        comRule.ApplicationName = rule.ProgramPath;
        comRule.Protocol = ProtocolTcp;
        comRule.Direction = rule.Direction == FirewallRuleDirection.Inbound ? DirectionIn : DirectionOut;
        comRule.Action = rule.Action == FirewallRuleAction.Allow ? ActionAllow : ActionBlock;
        comRule.Profiles = (int)rule.Profiles;
        comRule.Enabled = rule.Enabled;
        comRule.Grouping = RuleGroup;
        _policy.Rules.Add(comRule);
    }

    public void RemoveRule(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            _policy.Rules.Remove(name);
        }
        catch (COMException)
        {
            // Removing a rule that is not there is success, not failure: callers rely on this for idempotency.
        }
    }

    internal static object CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"Windows Firewall's COM object '{progId}' is not registered on this machine.");
        return Activator.CreateInstance(type)!;
    }

    // A rule this narrow mapping cannot make sense of (an unrecognised protocol or direction, or one COM would not
    // even return an ApplicationName for) is left out rather than guessed at: Weir's own logic only ever needs to
    // recognise its own rule and inbound block rules for its own program.
    private static FirewallRule? ToFirewallRule(dynamic comRule)
    {
        try
        {
            string? applicationName = comRule.ApplicationName;
            if (string.IsNullOrWhiteSpace(applicationName))
            {
                return null;
            }

            FirewallRuleDirection direction = (int)comRule.Direction switch
            {
                DirectionIn => FirewallRuleDirection.Inbound,
                DirectionOut => FirewallRuleDirection.Outbound,
                _ => FirewallRuleDirection.Outbound,
            };
            FirewallRuleAction action = (int)comRule.Action == ActionAllow ? FirewallRuleAction.Allow : FirewallRuleAction.Block;

            return new FirewallRule(
                (string)comRule.Name,
                applicationName,
                action,
                direction,
                (FirewallProfiles)(int)comRule.Profiles,
                (bool)comRule.Enabled);
        }
        catch (COMException)
        {
            return null;
        }
    }
}
