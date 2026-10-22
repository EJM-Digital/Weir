using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Endpoints;

namespace Weir.Api.Tests.Platform;

/// <summary>The live System overview when it cannot be read and when several screens ask for it at once.</summary>
public sealed class SystemOverviewStreamEdgesApiTests
{
    [Fact]
    public async Task A_reading_that_fails_sends_nothing_and_the_next_one_that_works_does()
    {
        await using var server = await WeirTestServer.StartAsync();
        var frames = server.Services.GetRequiredService<SystemOverviewFrames>();
        await TestDatabase.ExecuteAsync(server, "ALTER TABLE server_starts RENAME TO server_starts_away");

        var failed = await Record.ExceptionAsync(() => frames.RefreshAsync(CancellationToken.None));
        await TestDatabase.ExecuteAsync(server, "ALTER TABLE server_starts_away RENAME TO server_starts");
        await using var stream = frames.ForAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.Null(failed);
        Assert.True(await stream.MoveNextAsync());
        Assert.StartsWith("event: system.overview\n", stream.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Screens_that_ask_together_are_all_answered_and_the_next_stream_opens_on_the_one_reading_they_made()
    {
        await using var server = await WeirTestServer.StartAsync();
        var frames = server.Services.GetRequiredService<SystemOverviewFrames>();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => frames.RefreshAsync(CancellationToken.None))));

        await using var stream = frames.ForAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.StartsWith("event: system.overview\n", stream.Current, StringComparison.Ordinal);
    }
}
