using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Core.Media;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.Media;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>What Weir says when ffmpeg is not installed: the hardware page cannot list methods, and System counts the media tools as a check that fails.</summary>
public sealed class MissingMediaToolsApiTests
{
    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync(IMediaToolResolver tools)
    {
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton(tools));
        await TestDatabase.SeedAdminAsync(server);
        await server.WaitForStartupRecoveryAsync();
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Fact]
    public async Task The_hardware_page_says_ffmpeg_was_not_found_and_lists_no_methods()
    {
        var (server, client) = await StartAsync(new AbsentMediaTools());
        await using var _ = server;

        using var response = await client.GetAsync("/api/v1/processing/hardware");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.False(body["detected"]!.GetValue<bool>());
        Assert.Empty(body["available_methods"]!.AsArray());
        Assert.Equal("Weir could not find ffmpeg, so it cannot report acceleration methods. ffmpeg is not installed.", body["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task System_counts_one_check_fewer_as_passing_when_the_media_tools_are_missing()
    {
        var (found, foundClient) = await StartAsync(new PresentMediaTools());
        await using var _found = found;
        var (missing, missingClient) = await StartAsync(new AbsentMediaTools());
        await using var _missing = missing;

        var withTools = (await Json(await foundClient.GetAsync("/api/v1/system/overview")))["checks"]!;
        var withoutTools = (await Json(await missingClient.GetAsync("/api/v1/system/overview")))["checks"]!;

        Assert.Equal(withTools["total"]!.GetValue<int>(), withoutTools["total"]!.GetValue<int>());
        Assert.Equal(withTools["passing"]!.GetValue<int>() - 1, withoutTools["passing"]!.GetValue<int>());
    }

    [Fact]
    public async Task System_does_not_count_the_workers_as_passing_until_start_up_recovery_has_finished()
    {
        var recovery = new HeldRecovery();
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services => services.AddSingleton<ISwapJournal>(recovery));
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        await recovery.Reached.WaitAsync(Ceiling);

        var during = (await Json(await client.GetAsync("/api/v1/system/overview")))["checks"]!["passing"]!.GetValue<int>();
        recovery.Release();
        await server.WaitForStartupRecoveryAsync();
        var after = (await Json(await client.GetAsync("/api/v1/system/overview")))["checks"]!["passing"]!.GetValue<int>();

        Assert.Equal(after - 1, during);
    }

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private sealed class PresentMediaTools : IMediaToolResolver
    {
        public (string Ffprobe, string Ffmpeg) Resolve() => ("ffprobe", "ffmpeg");

        public string? ResolveMkvmerge() => null;
    }

    private sealed class AbsentMediaTools : IMediaToolResolver
    {
        public (string Ffprobe, string Ffmpeg) Resolve() => throw new MediaToolException("ffmpeg is not installed.");

        public string? ResolveMkvmerge() => null;
    }

    /// <summary>The swap sweep's journal read, which start-up recovery makes last: it stops there until released.</summary>
    private sealed class HeldRecovery : ISwapJournal
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Release() => _released.TrySetResult();

        public async Task<IReadOnlyList<SwapJournalEntry>> ListUnfinishedAsync(CancellationToken cancellationToken = default)
        {
            _reached.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<string>> ListKeepConflictPathsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task RecordAsync(SwapJournalEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
