using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weir.Api.Endpoints;
using Weir.Core.Activity;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Scheduling;
using static Weir.Api.Tests.Platform.LiveStreamApiTests;

namespace Weir.Api.Tests.Platform;

/// <summary>What the live stream says as it opens: a look the server already made, a newest event it cannot read yet, and how the tasks that feed it word a failure.</summary>
public sealed class ActivityStreamOpeningApiTests
{
    [Fact]
    public async Task A_stream_that_opens_after_the_server_has_looked_starts_with_when_it_last_did()
    {
        var (server, client) = await StartSignedInAsync();
        await using var _server = server;
        server.Services.GetRequiredService<ServerLooks>().ReadinessLooked();
        using var reader = await OpenStreamAsync(server, client);

        var frame = await NextFrameAsync(reader, "system.checks");

        Assert.NotNull(frame["readiness_checked_at"]);
    }

    [Fact]
    public async Task A_stream_that_cannot_read_the_newest_event_as_it_opens_tries_again_shortly_and_then_opens()
    {
        var clock = new FakeTimeProvider();
        var reads = 0;
        await using var frames = ActivityEndpoints.LatestFramesAsync(
            _ => ++reads == 1 ? throw new InvalidOperationException("the database is busy") : Task.FromResult<long?>(7),
            new ActivityLatestNotifier(),
            clock,
            TimeSpan.FromMinutes(1),
            NullLogger.Instance,
            CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());

        var opening = frames.MoveNextAsync().AsTask();
        await Eventually.ThatAdvancingAsync(clock, ActivityEndpoints.StreamReadRetry, () => opening.IsCompleted);

        Assert.True(await opening);
        Assert.Equal("event: activity.latest\ndata: {\"latest_event_id\":7,\"activity_revision\":0}\n\n", frames.Current);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task The_progress_stream_sends_nothing_while_no_file_moves_and_a_frame_when_one_does()
    {
        var store = new LiveProgressStore();
        var clock = new FakeTimeProvider();
        var idle = TimeSpan.FromMinutes(1);
        var waits = 0;
        await using var frames = ActivityProgressFrames.FramesAsync(
            () => store.Version,
            (version, wait, time, token) =>
            {
                waits++;
                return store.WaitForChangeAsync(version, wait, time, token);
            },
            store.Snapshot,
            clock,
            TimeSpan.Zero,
            idle,
            CancellationToken.None).GetAsyncEnumerator();

        var next = frames.MoveNextAsync().AsTask();
        await Eventually.ThatAdvancingAsync(clock, idle, () => waits >= 2);
        Assert.False(next.IsCompleted);
        store.Update("Film/Film.mkv", new LiveProgress(40, "Weir is writing the cleaned-up file.", 30.0, "processing", "120x", 5.0, [], [], "writing"));

        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.StartsWith("event: processing.progress\n", frames.Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ReadinessChangeTask), "Weir could not check whether it is ready.")]
    [InlineData(typeof(SystemOverviewChangeTask), "Weir could not read the facts System shows about it.")]
    public async Task A_task_that_feeds_the_stream_says_in_plain_words_what_it_could_not_do_and_waits_for_its_next_turn(Type task, string failure)
    {
        await using var server = await WeirTestServer.StartAsync();

        var registered = server.Services.GetServices<IPeriodicTask>().Single(candidate => candidate.GetType() == task);

        Assert.Equal(failure, registered.FailureMessage);
        Assert.Null(registered.FailureCooldown);
    }
}
