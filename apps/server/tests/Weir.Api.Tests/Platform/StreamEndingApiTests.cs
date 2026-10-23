using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Endpoints;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.SystemReadings;

namespace Weir.Api.Tests.Platform;

/// <summary>A live stream ends cleanly when its client goes away between two frames.</summary>
public sealed class StreamEndingApiTests
{
    [Fact]
    public async Task The_progress_stream_ends_when_the_client_goes_away_after_a_frame()
    {
        using var stop = new CancellationTokenSource();
        await using var frames = ActivityProgressFrames.FramesAsync(
            () => 0,
            (_, _, _, _) => Task.FromResult<long?>(1),
            () => new Dictionary<string, LiveProgress>(),
            TimeProvider.System,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1),
            stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.True(await frames.MoveNextAsync());

        await stop.CancelAsync();

        Assert.False(await frames.MoveNextAsync());
    }

    [Fact]
    public async Task The_system_stats_stream_ends_when_the_client_goes_away_after_a_frame()
    {
        var store = new SystemStatsStore(TimeProvider.System, 4);
        store.Add(StatsSample.Of(new StatsNow(DateTimeOffset.UtcNow, 12.4, 16, 8_000, 16_000, 1000, 500, 7.5, 3.1, 123_000, 41.0, 2_000, 1_500, 148.0, 2, 4)));
        using var stop = new CancellationTokenSource();
        await using var frames = new SystemStatsFrames(store).ForAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.True(await frames.MoveNextAsync());

        await stop.CancelAsync();

        Assert.False(await frames.MoveNextAsync());
    }

    [Fact]
    public async Task The_system_overview_stream_ends_when_the_client_goes_away_after_its_first_frame()
    {
        await using var server = await WeirTestServer.StartAsync();
        var overview = server.Services.GetRequiredService<SystemOverviewFrames>();
        using var stop = new CancellationTokenSource();
        await using var frames = overview.ForAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.True(await frames.MoveNextAsync());

        await stop.CancelAsync();

        Assert.False(await frames.MoveNextAsync());
    }
}
