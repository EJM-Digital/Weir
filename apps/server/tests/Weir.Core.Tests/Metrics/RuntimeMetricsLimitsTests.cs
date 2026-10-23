using Microsoft.Extensions.Time.Testing;
using Weir.Core.Metrics;

namespace Weir.Core.Tests.Metrics;

/// <summary>What the metrics keep and what they let go of: old samples, a flood of samples, and figures with no module to belong to.</summary>
public sealed class RuntimeMetricsLimitsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Noon);
    private readonly RuntimeMetricsStore _store;

    public RuntimeMetricsLimitsTests() => _store = new RuntimeMetricsStore(_time);

    [Fact]
    public void Only_the_newest_twenty_thousand_timings_are_kept()
    {
        for (var count = 0; count < 20_001; count++)
        {
            _store.RecordRequest("GET", "/r", 200, 1);
        }

        _store.RecordRequest("GET", "/r", 200, 500);

        Assert.Equal(20_002, _store.GetSummary().TotalRequests);
        Assert.Equal(1, _store.GetRequestFigures(Noon).MedianMs);
    }

    [Fact]
    public void Only_the_newest_ten_thousand_server_errors_are_kept()
    {
        for (var count = 0; count < 10_001; count++)
        {
            _store.RecordRequest("GET", "/r", 500, 1);
        }

        Assert.Equal(10_000, _store.GetRequestFigures(Noon.AddHours(-1)).ServerErrors);
    }

    [Fact]
    public void A_server_error_is_forgotten_after_two_days()
    {
        _store.RecordRequest("GET", "/r", 500, 1);
        _time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromMinutes(1));

        Assert.Equal(0, _store.GetRequestFigures(Noon).ServerErrors);
    }

    [Fact]
    public void Job_events_queue_depths_and_savings_for_no_module_are_ignored()
    {
        _store.RecordModuleJobEvent(string.Empty, "started");
        _store.RecordModuleJobEvent("processing", "exploded");
        _store.SetModuleQueueDepth(string.Empty, 4);
        _store.RecordModuleSavings(string.Empty, 100);
        _store.RecordModuleSavings("processing", 0);
        _store.RecordModuleSavings("processing", -5);

        var summary = _store.GetSummary();

        Assert.Empty(summary.ModuleJobCounts);
        Assert.Empty(summary.ModuleQueueDepths);
        Assert.Empty(summary.ModuleSavingsBytes);
    }
}
