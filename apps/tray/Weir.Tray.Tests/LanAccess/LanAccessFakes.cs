using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;

namespace Weir.Tray.Tests.LanAccess;

/// <summary>Windows Firewall as the tests need it: a rule that is there or not, and an answer to the administrator prompt.</summary>
internal sealed class FakeFirewall : IFirewallAccess
{
    public bool Allows { get; set; }

    public FirewallElevation.Outcome Answer { get; set; } = FirewallElevation.Outcome.Configured;

    public int Asked { get; private set; }

    public bool AllowsWeirIn() => Allows;

    public FirewallElevation.Outcome AskToAllow()
    {
        Asked++;
        if (Answer == FirewallElevation.Outcome.Configured)
        {
            Allows = true;
        }
        return Answer;
    }
}

/// <summary>The running server as far as LAN access steers it: where it listens, and whether a move to another scope works.</summary>
internal sealed class FakeServer(ListenScope scope) : IServerListenScope
{
    public ListenScope Scope { get; private set; } = scope;

    public ScopeChange ResultOfARealMove { get; set; } = ScopeChange.Applied;

    public List<ListenScope> Moves { get; } = [];

    public Task<ScopeChange> MoveToScopeAsync(ListenScope to, CancellationToken cancellationToken)
    {
        Moves.Add(to);
        if (to == Scope)
        {
            return Task.FromResult(ScopeChange.Unchanged);
        }
        if (ResultOfARealMove == ScopeChange.Applied)
        {
            Scope = to;
        }
        return Task.FromResult(ResultOfARealMove);
    }
}
