using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;
using Weir.Tray.Tests.Firewall;
using Xunit;

namespace Weir.Tray.Tests.LanAccess;

/// <summary>
/// Which devices the server listens for at start-up: a saved choice is final, and without one the answer follows
/// what Windows already allows, so an update never takes a reachable PC off the network and a fresh install is
/// never on it before anyone said so.
/// </summary>
public sealed class LanAccessStartupTests : IDisposable
{
    private const string InstallRoot = @"C:\Users\test\AppData\Local\Weir\current";
    private static readonly string ServerPath = WeirFirewallRule.ServerProgramPath(InstallRoot);

    private readonly TempDirectory _temp = TempDirectory.AsWeirHome();

    public void Dispose() => _temp.Dispose();

    private string Home => _temp.Path;

    private ListenScope Resolve(IFirewallPolicy policy) =>
        LanAccessStartup.Resolve(Home, InstallRoot, () => policy, _ => { });

    private static FirewallRule Inbound(string name, FirewallRuleAction action, FirewallProfiles profiles, bool enabled = true) =>
        new(name, ServerPath, action, FirewallRuleDirection.Inbound, profiles, enabled);

    // -- A saved choice is final ---------------------------------------------

    [Fact]
    public void A_saved_yes_wins_over_a_firewall_that_allows_nothing()
    {
        LanAccessSetting.Write(Home, ListenScope.OtherDevices);

        Assert.Equal(ListenScope.OtherDevices, Resolve(new FakeFirewallPolicy()));
    }

    [Fact]
    public void A_saved_no_wins_over_a_firewall_that_allows_the_server()
    {
        LanAccessSetting.Write(Home, ListenScope.ThisPcOnly);

        Assert.Equal(ListenScope.ThisPcOnly, Resolve(new FakeFirewallPolicy(WeirFirewallRule.DesiredRule(InstallRoot))));
    }

    [Fact]
    public void A_saved_choice_is_used_without_reading_the_firewall()
    {
        LanAccessSetting.Write(Home, ListenScope.ThisPcOnly);
        IFirewallPolicy NeverOpen() => throw new Xunit.Sdk.XunitException("The firewall must not be read when a choice is saved.");

        var scope = LanAccessStartup.Resolve(Home, InstallRoot, NeverOpen, _ => { });

        Assert.Equal(ListenScope.ThisPcOnly, scope);
    }

    // -- The one-off migration, from each firewall state --------------------------

    [Fact]
    public void With_no_choice_and_no_rule_the_server_listens_for_this_pc_only_and_that_is_saved()
    {
        var scope = Resolve(new FakeFirewallPolicy());

        Assert.Equal(ListenScope.ThisPcOnly, scope);
        Assert.Equal(ListenScope.ThisPcOnly, LanAccessSetting.Read(Home, _ => { }));
    }

    [Fact]
    public void With_no_choice_and_weirs_own_rule_the_server_listens_for_other_devices_and_that_is_saved()
    {
        var scope = Resolve(new FakeFirewallPolicy(WeirFirewallRule.DesiredRule(InstallRoot)));

        Assert.Equal(ListenScope.OtherDevices, scope);
        Assert.Equal(ListenScope.OtherDevices, LanAccessSetting.Read(Home, _ => { }));
    }

    [Fact]
    public void With_no_choice_and_a_rule_windows_made_itself_the_server_listens_for_other_devices()
    {
        var policy = new FakeFirewallPolicy(Inbound("WeirServer.exe", FirewallRuleAction.Allow, FirewallProfiles.Private | FirewallProfiles.Public));

        Assert.Equal(ListenScope.OtherDevices, Resolve(policy));
    }

    [Fact]
    public void With_no_choice_and_a_disabled_allow_rule_the_server_listens_for_this_pc_only()
    {
        var policy = new FakeFirewallPolicy(Inbound("Weir", FirewallRuleAction.Allow, FirewallProfiles.Private, enabled: false));

        Assert.Equal(ListenScope.ThisPcOnly, Resolve(policy));
    }

    [Fact]
    public void With_no_choice_and_only_a_block_rule_the_server_listens_for_this_pc_only()
    {
        var policy = new FakeFirewallPolicy(Inbound("Blocked WeirServer.exe", FirewallRuleAction.Block, FirewallProfiles.Private));

        Assert.Equal(ListenScope.ThisPcOnly, Resolve(policy));
    }

    [Fact]
    public void With_no_choice_a_block_rule_on_the_same_network_as_the_allow_rule_keeps_the_server_on_this_pc_only()
    {
        var policy = new FakeFirewallPolicy(
            Inbound("Weir", FirewallRuleAction.Allow, FirewallProfiles.Private),
            Inbound("Blocked WeirServer.exe", FirewallRuleAction.Block, FirewallProfiles.Private));

        Assert.Equal(ListenScope.ThisPcOnly, Resolve(policy));
    }

    [Fact]
    public void With_no_choice_the_migration_happens_once_and_a_later_rule_change_does_not_undo_it()
    {
        Resolve(new FakeFirewallPolicy(WeirFirewallRule.DesiredRule(InstallRoot)));

        Assert.Equal(ListenScope.OtherDevices, Resolve(new FakeFirewallPolicy()));
    }

    [Fact]
    public void A_choice_that_cannot_be_saved_is_still_followed_and_the_log_says_it_is_worked_out_again()
    {
        var folder = Path.Combine(Home, "refused");
        using var denied = new DeniedFolder(folder, System.Security.AccessControl.FileSystemRights.CreateFiles);
        var logged = new List<string>();

        var scope = LanAccessStartup.Resolve(folder, InstallRoot, () => new FakeFirewallPolicy(WeirFirewallRule.DesiredRule(InstallRoot)), logged.Add);

        Assert.Equal(ListenScope.OtherDevices, scope);
        Assert.Contains(logged, line => line.Contains("the choice could not be saved", StringComparison.Ordinal) && line.Contains("worked out again at the next start", StringComparison.Ordinal));
    }

    [Fact]
    public void A_firewall_that_cannot_be_read_means_this_pc_only_for_now_and_nothing_is_saved()
    {
        IFirewallPolicy Unreadable() => throw new InvalidOperationException("The firewall service is not running.");

        var scope = LanAccessStartup.Resolve(Home, InstallRoot, Unreadable, _ => { });

        Assert.Equal(ListenScope.ThisPcOnly, scope);
        Assert.Null(LanAccessSetting.Read(Home, _ => { }));
    }

    [Fact]
    public void The_decision_and_its_reason_are_logged()
    {
        var messages = new List<string>();

        LanAccessStartup.Resolve(Home, InstallRoot, () => new FakeFirewallPolicy(WeirFirewallRule.DesiredRule(InstallRoot)), messages.Add);

        Assert.Contains(messages, m => m.Contains("other devices on the network can connect", StringComparison.Ordinal)
            && m.Contains("already allows", StringComparison.Ordinal));
    }
}
