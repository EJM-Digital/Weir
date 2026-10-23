using Weir.Core.Metrics;

namespace Weir.Infrastructure.Jobs;

/// <summary>Queue counters for metrics: job events and queue depth per module.</summary>
public interface IJobQueueMetrics
{
    /// <summary><paramref name="jobEvent"/> is <c>started</c>, <c>completed</c> or <c>failed</c>.</summary>
    void RecordJobEvent(string moduleName, string jobEvent);

    void SetQueueDepth(string moduleName, int depth);
}

/// <summary>Discards queue counters when no metrics sink is configured.</summary>
public sealed class NoJobQueueMetrics : IJobQueueMetrics
{
    public static NoJobQueueMetrics Instance { get; } = new();

    public void RecordJobEvent(string moduleName, string jobEvent)
    {
    }

    public void SetQueueDepth(string moduleName, int depth)
    {
    }
}

/// <summary>Feeds the job queue's counters into <see cref="RuntimeMetricsStore"/>, for System and the Prometheus scrape.</summary>
public sealed class RuntimeJobQueueMetrics : IJobQueueMetrics
{
    private readonly RuntimeMetricsStore _metrics;

    public RuntimeJobQueueMetrics(RuntimeMetricsStore metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    public void RecordJobEvent(string moduleName, string jobEvent) => _metrics.RecordModuleJobEvent(moduleName, jobEvent);

    public void SetQueueDepth(string moduleName, int depth) => _metrics.SetModuleQueueDepth(moduleName, depth);
}
