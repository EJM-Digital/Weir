using System.Collections;
using System.Runtime.InteropServices;
using Weir.Tray.Firewall;
using Xunit;

namespace Weir.Tray.Tests.Firewall;

/// <summary>
/// Windows Firewall through its COM objects: read for real on this PC (reading needs no administrator rights), and written
/// through a stand-in policy that takes the rule Windows' own rule object was filled in as, so nothing on this PC changes.
/// </summary>
public sealed class ComFirewallPolicyTests
{
    private const string Program = @"C:\Weir\server\WeirServer.exe";

    [Fact]
    public void The_real_firewall_is_read_without_administrator_rights()
    {
        var policy = new ComFirewallPolicy();

        var rules = policy.Rules;

        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.ProgramPath)));
        Assert.NotEqual(FirewallProfiles.None, policy.CurrentProfiles);
    }

    [Fact]
    public void A_COM_object_that_is_not_registered_is_named_in_the_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ComFirewallPolicy.CreateComObject("Weir.NotARegisteredObject"));

        Assert.Contains("Weir.NotARegisteredObject", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true, 1, 1)]
    [InlineData(false, false, false, 2, 0)]
    public void A_rule_is_written_to_Windows_own_rule_object_and_replaces_one_of_the_same_name(
        bool inbound, bool allow, bool enabled, int comDirection, int comAction)
    {
        var rules = new StandInRuleCollection();
        var policy = new ComFirewallPolicy(new StandInPolicy(rules, profiles: 0), ComFirewallPolicy.CreateComObject);

        policy.AddOrUpdateRule(new FirewallRule(
            "Weir",
            Program,
            allow ? FirewallRuleAction.Allow : FirewallRuleAction.Block,
            inbound ? FirewallRuleDirection.Inbound : FirewallRuleDirection.Outbound,
            FirewallProfiles.Private | FirewallProfiles.Public,
            enabled));

        Assert.Equal(["remove Weir", "add"], rules.Calls);
        dynamic written = rules.Added!;
        Assert.Equal("Weir", (string)written.Name);
        Assert.Equal(Program, (string)written.ApplicationName);
        Assert.Equal(6, (int)written.Protocol);
        Assert.Equal(comDirection, (int)written.Direction);
        Assert.Equal(comAction, (int)written.Action);
        Assert.Equal(6, (int)written.Profiles);
        Assert.Equal(enabled, (bool)written.Enabled);
        Assert.Equal("Weir", (string)written.Grouping);
        Assert.Contains("Managed by Weir", (string)written.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_rule_that_is_not_there_is_success_and_a_missing_name_is_refused()
    {
        var rules = new StandInRuleCollection { RemoveThrows = true };
        var policy = new ComFirewallPolicy(new StandInPolicy(rules, profiles: 0), ComFirewallPolicy.CreateComObject);

        policy.RemoveRule("Weir");

        Assert.Equal(["remove Weir"], rules.Calls);
        Assert.Throws<ArgumentNullException>(() => policy.RemoveRule(null!));
        Assert.Throws<ArgumentNullException>(() => policy.AddOrUpdateRule(null!));
    }

    [Fact]
    public void The_firewalls_rules_are_read_into_Weirs_own_terms_and_a_rule_that_cannot_be_read_is_left_out()
    {
        var rules = new StandInRuleCollection();
        rules.Existing.AddRange(
        [
            new StandInRule { Name = "Weir", ApplicationName = Program, Direction = 1, Action = 1, Profiles = 7, Enabled = true },
            new StandInRule { Name = "Out", ApplicationName = Program, Direction = 2, Action = 0, Profiles = 2, Enabled = false },
            new StandInRule { Name = "Odd direction", ApplicationName = Program, Direction = 3, Action = 0, Profiles = 1, Enabled = true },
            new StandInRule { Name = "No program", ApplicationName = " ", Direction = 1, Action = 1, Profiles = 7, Enabled = true },
            new StandInRule { Name = "Not a program", ApplicationName = null, Direction = 1, Action = 1, Profiles = 7, Enabled = true },
            new StandInRule { Name = "Unreadable", ApplicationName = Program, Direction = 1, Action = 1, Profiles = 7, Enabled = true, UnreadableDirection = true },
        ]);
        var policy = new ComFirewallPolicy(new StandInPolicy(rules, profiles: 6), ComFirewallPolicy.CreateComObject);

        Assert.Equal(
            [
                new FirewallRule("Weir", Program, FirewallRuleAction.Allow, FirewallRuleDirection.Inbound, FirewallProfiles.Domain | FirewallProfiles.Private | FirewallProfiles.Public, true),
                new FirewallRule("Out", Program, FirewallRuleAction.Block, FirewallRuleDirection.Outbound, FirewallProfiles.Private, false),
                new FirewallRule("Odd direction", Program, FirewallRuleAction.Block, FirewallRuleDirection.Outbound, FirewallProfiles.Domain, true),
            ],
            policy.Rules);
        Assert.Equal(FirewallProfiles.Private | FirewallProfiles.Public, policy.CurrentProfiles);
    }

#pragma warning disable CA2201 // A COM call that fails throws COMException; the stand-ins have to throw the same.
    private static COMException ComFailure(string message) => new(message);
#pragma warning restore CA2201

    public sealed class StandInPolicy(StandInRuleCollection rules, int profiles)
    {
        public StandInRuleCollection Rules { get; } = rules;

        public int CurrentProfileTypes { get; } = profiles;
    }

    public sealed class StandInRuleCollection : IEnumerable<StandInRule>
    {
        public List<string> Calls { get; } = [];

        public List<StandInRule> Existing { get; } = [];

        public bool RemoveThrows { get; init; }

        public object? Added { get; private set; }

        public void Add(object rule)
        {
            Calls.Add("add");
            Added = rule;
        }

        public void Remove(string name)
        {
            Calls.Add($"remove {name}");
            if (RemoveThrows)
            {
                throw ComFailure("No rule with that name.");
            }
        }

        public IEnumerator<StandInRule> GetEnumerator() => Existing.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class StandInRule
    {
        public string Name { get; init; } = "";

        public string? ApplicationName { get; init; }

        public int Action { get; init; }

        public int Profiles { get; init; }

        public bool Enabled { get; init; }

        public bool UnreadableDirection { get; init; }

        public int Direction
        {
            get => UnreadableDirection ? throw ComFailure("The rule could not be read.") : field;
            init;
        }
    }
}
