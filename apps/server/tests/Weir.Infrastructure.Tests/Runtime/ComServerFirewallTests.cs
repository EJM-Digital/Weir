using System.Runtime.Versioning;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>Windows Firewall's own policy object, read the way the Windows package reads it. Reading needs no administrator rights.</summary>
public sealed class ComServerFirewallTests
{
    [SupportedOSPlatform("windows")]
    [WindowsFact("Windows Firewall's COM policy object exists only on Windows")]
    public void The_real_firewall_has_no_allow_rule_for_a_program_that_is_not_weirs_server()
    {
        // The rules are matched on the path of WeirServer.exe next to the running program, and the test host has no such file.
        Assert.Equal(FirewallVerdict.Blocks, new ComServerFirewall().Judge());
    }

    [SupportedOSPlatform("windows")]
    [WindowsTheory("Windows Firewall's COM policy object exists only on Windows")]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    [InlineData(typeof(InvalidOperationException))]
    public void A_firewall_that_cannot_be_read_is_reported_as_not_letting_other_devices_in(Type failure)
    {
        var firewall = new ComServerFirewall(() => throw (Exception)Activator.CreateInstance(failure)!);

        Assert.Equal(FirewallVerdict.Blocks, firewall.Judge());
    }

    [SupportedOSPlatform("windows")]
    [WindowsFact("Windows Firewall's COM policy object exists only on Windows")]
    public void A_rule_for_the_server_program_is_read_as_it_is_and_one_for_another_program_or_direction_is_not()
    {
        const string Program = @"C:\Weir\WeirServer.exe";

        Assert.Equal(new ServerFirewallRule(true, true, 3), ComServerFirewall.TryRead(new FakeRule(1, Program, 1, true, 3), Program));
        Assert.Equal(new ServerFirewallRule(false, false, 2), ComServerFirewall.TryRead(new FakeRule(1, Program.ToUpperInvariant(), 0, false, 2), Program));
        Assert.Null(ComServerFirewall.TryRead(new FakeRule(2, Program, 1, true, 3), Program));
        Assert.Null(ComServerFirewall.TryRead(new FakeRule(1, @"C:\Other\Thing.exe", 1, true, 3), Program));
    }

    [SupportedOSPlatform("windows")]
    [WindowsFact("Windows Firewall's COM policy object exists only on Windows")]
    public void A_rule_the_firewall_will_not_describe_is_skipped_not_fatal()
    {
        Assert.Null(ComServerFirewall.TryRead(new UnreadableRule(), @"C:\Weir\WeirServer.exe"));
    }

    public sealed record FakeRule(int Direction, string ApplicationName, int Action, bool Enabled, int Profiles);

    public sealed class UnreadableRule
    {
#pragma warning disable CA2201 // The firewall raises this type itself; it is what the code under test handles.
        public int Direction => throw new System.Runtime.InteropServices.COMException("The rule is being changed.");
#pragma warning restore CA2201
    }
}
