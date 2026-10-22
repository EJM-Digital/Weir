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
}
