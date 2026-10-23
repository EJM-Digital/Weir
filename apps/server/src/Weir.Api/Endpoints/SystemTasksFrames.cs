using Weir.Core.Json;
using Weir.Infrastructure.Scheduling;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>system.tasks</c> side of the Activity stream: the whole list of scheduled tasks, sent each time a task starts or ends
/// or one comes or goes, so the Scheduled tasks card changes without polling. Changes that arrive while a frame is being
/// written collapse into the next frame, which always carries the newest list.
/// </summary>
public sealed class SystemTasksFrames
{
    private readonly PeriodicTaskRegistry _tasks;

    public SystemTasksFrames(PeriodicTaskRegistry tasks) => _tasks = tasks;

    /// <summary>The frames for one open stream, from when it opens until it ends.</summary>
    public async IAsyncEnumerable<string> ForAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = _tasks.SubscribeToChanges();
        while (!cancellationToken.IsCancellationRequested)
        {
            await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
            yield return Frame(_tasks.Snapshot());
        }
    }

    /// <summary>The <c>system.tasks</c> SSE frame: every scheduled task.</summary>
    public static string Frame(IReadOnlyList<PeriodicTaskStatus> tasks) =>
        $"event: system.tasks\ndata: {WireJsonWriter.Dumps(SystemTasksWire.List(tasks), WireJsonFormat.Compact)}\n\n";
}
