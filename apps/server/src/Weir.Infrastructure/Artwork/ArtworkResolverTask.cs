using Weir.Core.Artwork;
using Weir.Infrastructure.Scheduling;

namespace Weir.Infrastructure.Artwork;

/// <summary>
/// <c>artwork-resolver</c>: every few seconds, titles any new files and looks up the posters and original languages that are due. It
/// looks nothing up when no gateway is configured, and a pass is cut short when the service is busy. About once an hour it also
/// prunes what files that are gone leave behind, which needs no network.
/// </summary>
public sealed class ArtworkResolverTask : IPeriodicTask
{
    /// <summary>The most titles one pass looks up, so a pass ends in good time.</summary>
    private const int MaxLookupsPerPass = 20;

    private readonly ArtworkGatewayClient _gateway;
    private readonly ArtworkDiscovery _discovery;
    private readonly ArtworkResolver _resolver;
    private readonly ArtworkPruner _pruner;
    private readonly TimeProvider _time;
    private DateTimeOffset _nextPruneAt = DateTimeOffset.MinValue;

    public ArtworkResolverTask(
        ArtworkGatewayClient gateway, ArtworkDiscovery discovery, ArtworkResolver resolver, ArtworkPruner pruner, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(pruner);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(resolver);
        _pruner = pruner;
        _time = time;
        _gateway = gateway;
        _discovery = discovery;
        _resolver = resolver;
    }

    public string Name => "artwork-resolver";

    public string? Label => null;

    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    public bool RunAtStart => false;

    public TimeSpan? FailureCooldown => TimeSpan.FromSeconds(30);

    public string FailureMessage => "Looking up posters failed; Weir will try again shortly";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await PruneWhenDueAsync(cancellationToken).ConfigureAwait(false);
        if (!_gateway.IsConfigured)
        {
            return;
        }

        await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        await _resolver.ResolveDueAsync(MaxLookupsPerPass, cancellationToken).ConfigureAwait(false);
    }

    private async Task PruneWhenDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (now < _nextPruneAt)
        {
            return;
        }

        _nextPruneAt = now + ArtworkSchedule.PruneInterval;
        await _pruner.PruneAsync(cancellationToken).ConfigureAwait(false);
    }

}
