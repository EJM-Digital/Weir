using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Api.Tests;

/// <summary>
/// Start-up recovery runs on a thread of its own after Kestrel is listening, so a server that is up but not
/// finished starting must say so: readiness is 503 and "starting" until recovery ends, whether or not any
/// worker is switched on, and 200 once it has.
/// </summary>
public sealed class StartupRecoveryReadinessTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task Readiness_is_starting_until_start_up_recovery_ends_and_ready_after(string workerCount)
    {
        var recovery = new HeldRecovery();
        await using var server = await WeirTestServer.StartAsync(
            [("WEIR_PROCESSING_WORKER_COUNT", workerCount)],
            signedIn: true,
            configureServices: services => services.AddSingleton<ISwapJournal>(recovery));
        await recovery.Reached.WaitAsync(Ceiling);

        var (detailedStatus, detailed) = await ReadAsync(server, "/api/v1/system/readiness");
        var (briefStatus, brief) = await ReadAsync(server, "/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, detailedStatus);
        Assert.False(detailed.GetProperty("ready").GetBoolean());
        Assert.Equal("starting", detailed.GetProperty("status").GetString());
        var workers = detailed.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("name").GetString() == "workers");
        Assert.Equal("starting", workers.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, briefStatus);
        Assert.Equal("starting", brief.GetProperty("status").GetString());

        recovery.Release();
        await server.WaitForStartupRecoveryAsync();

        // Workers beat a moment after recovery, so with workers on the lanes may still be settling.
        var (readyStatus, ready) = await ReadUntilReadyAsync(server);
        Assert.Equal(HttpStatusCode.OK, readyStatus);
        Assert.True(ready.GetProperty("ready").GetBoolean());
        Assert.Equal("ready", ready.GetProperty("status").GetString());
    }

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(WeirTestServer server, string path)
    {
        using var response = await server.Client.GetAsync(path);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadUntilReadyAsync(WeirTestServer server) =>
        await Eventually.PollAsync(() => ReadAsync(server, "/ready"), result => result.Status == HttpStatusCode.OK, Ceiling);

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
