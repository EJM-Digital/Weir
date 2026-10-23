using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Whether the opt-in <c>reject</c> failure policy can be offered for a library linked to a set of
/// manager connections. A manager whose port removes queue items can always be asked (the safety rules are applied per
/// file at run time); any other manager must advertise <see cref="RejectSupportRules.RejectCapability"/>.
/// </summary>
public sealed class RejectSupportEvaluator
{
    private readonly IMediaManagerPorts _ports;

    public RejectSupportEvaluator(IMediaManagerPorts ports)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    public async Task<RejectSupportResult> EvaluateAsync(IReadOnlyList<ManagerConnection> connections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (connections.Count == 0)
        {
            return new RejectSupportResult(
                false,
                "Link a media manager to this workflow first. Rejecting needs a manager that can find a different release.");
        }

        var reasons = new List<string>();
        foreach (var connection in connections)
        {
            var port = _ports.PortForKind(connection.Kind);
            if (port is null)
            {
                continue;
            }

            if (port.Capabilities().RemovesQueueItems)
            {
                return new RejectSupportResult(
                    true,
                    $"{connection.Label} can remove the download, blocklist the release and search for another. " +
                    "Weir only does this when the download holds just this one file; otherwise it hands the original back.");
            }

            var description = await port.DescribeAsync(connection, cancellationToken).ConfigureAwait(false);
            if (description.Status != SignalStatus.Reported)
            {
                reasons.Add(description.Detail ?? $"Weir could not ask {connection.Label} what it can do.");
                continue;
            }

            if (description.Advertised.Contains(RejectSupportRules.RejectCapability))
            {
                return new RejectSupportResult(
                    true,
                    $"{connection.Label} says it can blocklist a bad release and find another. Weir removes " +
                    "the download only after it accepts the report.");
            }

            reasons.Add(
                $"{connection.Label} does not yet say it can replace a rejected release, so rejecting would " +
                "delete a download with nothing coming to replace it.");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("None of the linked media managers can take a rejection.");
        }

        return new RejectSupportResult(false, string.Join(" ", reasons));
    }
}
