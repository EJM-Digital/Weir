using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Scheduling;

namespace Weir.Api.Endpoints;

/// <summary>
/// <c>system-overview</c>: while a browser is watching, reads the facts System shows about Weir every few seconds and sends the
/// new overview to every open stream when one has changed (<see cref="SystemOverviewFrames"/>). Idle, with no stream open, it does
/// not touch the database.
/// </summary>
internal sealed class SystemOverviewChangeTask : IPeriodicTask
{
    private readonly SystemOverviewFrames _frames;
    private readonly ActivityStreamClients _clients;

    public SystemOverviewChangeTask(SystemOverviewFrames frames, ActivityStreamClients clients)
    {
        _frames = frames;
        _clients = clients;
    }

    public string Name => "system-overview";

    public string? Label => null;

    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    public bool RunAtStart => false;

    public TimeSpan? FailureCooldown => null;

    public string FailureMessage => "Weir could not read the facts System shows about it.";

    public Task RunOnceAsync(CancellationToken cancellationToken) =>
        _clients.Count == 0 ? Task.CompletedTask : _frames.RefreshAsync(cancellationToken);
}
