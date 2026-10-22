using Weir.Core.Readiness;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Scheduling;

namespace Weir.Api.Endpoints;

/// <summary>
/// <c>readiness-changes</c>: while a browser is watching, looks at <c>GET /system/readiness</c>'s answer every few seconds and says so
/// on <see cref="DataTopics.Readiness"/> when it is not the one it gave last time, so a worker that stops, or a start that
/// finishes, shows on every open screen without anyone asking. The first look counts as a change, because a page that opened a
/// moment earlier may have read an older answer. Each look is told to the open streams as the time Health shows
/// (<see cref="ServerLooks"/>). Idle, with no stream open, it does not touch the database.
/// </summary>
internal sealed class ReadinessChangeTask : IPeriodicTask
{
    private readonly IServiceProvider _services;
    private readonly ActivityStreamClients _clients;
    private readonly DataChangePublisher _changes;
    private readonly ServerLooks _looks;
    private string? _answer;

    public ReadinessChangeTask(IServiceProvider services, ActivityStreamClients clients, DataChangePublisher changes, ServerLooks looks)
    {
        _services = services;
        _clients = clients;
        _changes = changes;
        _looks = looks;
    }

    public string Name => "readiness-changes";

    public string? Label => null;

    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    public bool RunAtStart => false;

    public TimeSpan? FailureCooldown => null;

    public string FailureMessage => "Weir could not check whether it is ready.";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (_clients.Count == 0)
        {
            return;
        }

        var answer = AnswerOf(await SystemEndpoints.BuildReadinessAsync(_services).ConfigureAwait(false));
        _looks.ReadinessLooked();
        if (answer == _answer)
        {
            return;
        }

        _answer = answer;
        _changes.Publish(DataTopics.Readiness);
    }

    /// <summary>What a screen would show of the report: whether Weir is ready, why, and how each worker lane is. Not the seconds it took to start.</summary>
    internal static string AnswerOf(ReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var steps = report.Steps.Select(step => $"{step.Name}|{step.Status}|{step.Detail}");
        var lanes = report.WorkerHealth.Select(lane =>
            $"{lane.Module}|{lane.ExpectedWorkers}|{lane.ActiveWorkers}|{lane.StaleWorkers}|{lane.StoppedWorkers}|{lane.Status}|{lane.Detail}");
        return string.Join('\n', [$"{report.Ready}|{report.Status}", .. steps, .. lanes]);
    }
}
