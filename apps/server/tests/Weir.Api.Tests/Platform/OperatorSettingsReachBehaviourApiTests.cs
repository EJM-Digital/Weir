using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Core.Jobs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// A setting saved through <c>PUT /processing/operator-settings</c> is the one the work reads: the resolution budget decides what
/// a file costs and whether it starts, the work file sweep and the unclaimed hand-back cleanup follow their saved switch and
/// interval, and the cleanup's saved window decides which copies it removes. Each test saves over HTTP and then asks the real
/// part of the server that acts on it.
/// </summary>
public sealed class OperatorSettingsReachBehaviourApiTests
{
    private const string Path = "/api/v1/processing/operator-settings";
    private const string Remux = "processing.file.remux_pass.v1";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> SignedInAsync()
    {
        var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static async Task SaveAsync(ApiTestClient client, params (string Name, object Value)[] settings)
    {
        var body = new Dictionary<string, object> { ["csrf_token"] = await client.CsrfAsync() };
        foreach (var (name, value) in settings)
        {
            body[name] = value;
        }

        using var response = await client.PutAsync(Path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<long> AddLibraryAsync(WeirTestServer server, string name = "Films")
    {
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO libraries (name, enabled, media_type, watched_folder, work_folder, output_folder, max_concurrent_files) " +
            "VALUES (@name, 1, 'movie', @watched, '', @output, 0)",
            ("@name", name), ("@watched", System.IO.Path.Join(server.Home, "in")), ("@output", System.IO.Path.Join(server.Home, "out")));
        return await TestDatabase.ScalarAsync(server, "SELECT id FROM libraries WHERE name = @name", ("@name", name));
    }

    /// <summary>A file the library knows by its size in pixels, as an earlier scan or pass leaves it.</summary>
    private static Task AddFileAsync(WeirTestServer server, long library, string relativePath, int width, int height) =>
        TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO files (library_id, relative_path, video_width, video_height) VALUES (@library, @path, @width, @height)",
            ("@library", library), ("@path", relativePath), ("@width", width), ("@height", height));

    private static Task<ProcessingJob> QueuePassAsync(WeirTestServer server, long library, string relativePath) =>
        server.Services.GetRequiredService<ProcessingJobStore>().EnqueueOrGetAsync(
            $"pass:{relativePath}", Remux, $$"""{"library_id":{{library}},"relative_media_path":"{{relativePath}}"}""");

    /// <summary>Claims the next file pass the way a worker does; the periodic sweeps the server queues on its own are not file passes.</summary>
    private static Task<ProcessingJob?> ClaimAsync(WeirTestServer server, string owner)
    {
        var now = DateTimeOffset.UtcNow;
        return server.Services.GetRequiredService<ProcessingJobStore>().ClaimNextAdmittedAsync(owner, now.AddHours(1), now, new ClaimableKinds([Remux], IncludeRefusedKinds: false));
    }

    [Fact]
    public async Task A_file_pass_is_charged_the_resolution_cost_that_was_saved()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        var library = await AddLibraryAsync(server);
        await AddFileAsync(server, library, "dvd.mkv", 720, 480);
        await AddFileAsync(server, library, "hd.mkv", 1280, 720);
        await AddFileAsync(server, library, "full-hd.mkv", 1920, 1080);
        await AddFileAsync(server, library, "uhd.mkv", 3840, 2160);
        await SaveAsync(client, ("runner_capacity", 64), ("runner_cost_sd", 11), ("runner_cost_720p", 22), ("runner_cost_1080p", 33), ("runner_cost_4k", 44));

        var costs = new List<int>();
        foreach (var file in new[] { "dvd.mkv", "hd.mkv", "full-hd.mkv", "uhd.mkv" })
        {
            costs.Add((await QueuePassAsync(server, library, file)).RunnerCost);
        }

        Assert.Equal(new[] { 11, 22, 33, 44 }, costs);
    }

    [Fact]
    public async Task The_saved_capacity_and_switch_decide_whether_a_costly_file_starts()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        var library = await AddLibraryAsync(server);
        await AddFileAsync(server, library, "uhd.mkv", 3840, 2160);
        await AddFileAsync(server, library, "other-uhd.mkv", 3840, 2160);
        await SaveAsync(client, ("max_concurrent_files", 10), ("runner_budget_enabled", true), ("runner_capacity", 4), ("runner_cost_4k", 3));
        await QueuePassAsync(server, library, "uhd.mkv");
        await QueuePassAsync(server, library, "other-uhd.mkv");

        Assert.NotNull(await ClaimAsync(server, "first"));
        // 3 of the 4 units are taken, and another 4K file needs 3.
        Assert.Null(await ClaimAsync(server, "second"));

        await SaveAsync(client, ("runner_capacity", 6));
        Assert.NotNull(await ClaimAsync(server, "third"));
    }

    [Fact]
    public async Task Switching_the_budget_off_lets_a_costly_file_start_whatever_it_costs()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        var library = await AddLibraryAsync(server);
        await AddFileAsync(server, library, "uhd.mkv", 3840, 2160);
        await SaveAsync(client, ("max_concurrent_files", 10), ("runner_budget_enabled", true), ("runner_capacity", 2), ("runner_cost_4k", 5));
        await QueuePassAsync(server, library, "uhd.mkv");

        Assert.Null(await ClaimAsync(server, "held"));

        await SaveAsync(client, ("runner_budget_enabled", false));
        Assert.NotNull(await ClaimAsync(server, "started"));
    }

    [Fact]
    public async Task The_work_file_sweep_follows_the_saved_switch_and_interval_for_both_scopes()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        var sweeps = server.Services.GetServices<IPeriodicEnqueuer>().OfType<WorkTempStaleSweepEnqueuer>().ToList();
        Assert.Equal(2, sweeps.Count);

        await SaveAsync(client, ("work_temp_stale_sweep_enabled", false), ("work_temp_stale_sweep_interval_seconds", 5400));

        foreach (var sweep in sweeps)
        {
            Assert.False(await sweep.IsEnabledAsync(CancellationToken.None), sweep.Name);
            Assert.Equal(TimeSpan.FromSeconds(5400), await sweep.IntervalAsync(CancellationToken.None));
        }

        await SaveAsync(client, ("work_temp_stale_sweep_enabled", true), ("work_temp_stale_sweep_interval_seconds", 900));

        foreach (var sweep in sweeps)
        {
            Assert.True(await sweep.IsEnabledAsync(CancellationToken.None), sweep.Name);
            Assert.Equal(TimeSpan.FromMinutes(15), await sweep.IntervalAsync(CancellationToken.None));
        }

        using var maintenance = await client.GetAsync("/api/v1/processing/maintenance");
        var family = (await ApiTestClient.Json(maintenance))["families"]!.AsArray().Single(f => f!["family"]!.GetValue<string>() == "work_temp_stale_sweep")!;
        Assert.True(family["enabled"]!.GetValue<bool>());
        Assert.Equal(900, family["interval_seconds"]!.GetValue<long>());
    }

    [Fact]
    public async Task The_unclaimed_cleanup_follows_the_saved_switch_and_interval_for_both_scopes()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        var cleanups = server.Services.GetServices<IPeriodicEnqueuer>().OfType<UnclaimedHandbackCleanupEnqueuer>().ToList();
        Assert.Equal(2, cleanups.Count);
        foreach (var cleanup in cleanups)
        {
            Assert.False(await cleanup.IsEnabledAsync(CancellationToken.None), $"{cleanup.Name} starts off");
        }

        await SaveAsync(client, ("unclaimed_handback_cleanup_enabled", true), ("unclaimed_handback_cleanup_interval_seconds", 7200));

        foreach (var cleanup in cleanups)
        {
            Assert.True(await cleanup.IsEnabledAsync(CancellationToken.None), cleanup.Name);
            Assert.Equal(TimeSpan.FromHours(2), await cleanup.IntervalAsync(CancellationToken.None));
        }

        await SaveAsync(client, ("unclaimed_handback_cleanup_enabled", false));

        foreach (var cleanup in cleanups)
        {
            Assert.False(await cleanup.IsEnabledAsync(CancellationToken.None), cleanup.Name);
        }
    }

    [Fact]
    public async Task The_unclaimed_cleanup_removes_a_copy_only_once_it_is_older_than_the_saved_window()
    {
        var (server, client) = await SignedInAsync();
        await using var disposeServer = server;
        var library = await AddLibraryAsync(server);
        var copy = await HandedBackAsync(server, library, "Film/film.mkv", daysAgo: 5);
        var handler = server.Services.GetServices<IJobHandler>().OfType<UnclaimedHandbackCleanupHandler>().Single();

        await SaveAsync(client, ("unclaimed_handback_window_days", 7));
        await RunCleanupAsync(handler);
        Assert.True(File.Exists(copy), "A copy five days old is inside a seven day window.");

        await SaveAsync(client, ("unclaimed_handback_window_days", 4));
        await RunCleanupAsync(handler);
        Assert.False(File.Exists(copy), "A copy five days old is outside a four day window.");
    }

    /// <summary>A copy Weir wrote <paramref name="daysAgo"/> days ago, recorded as a pass records it.</summary>
    private static async Task<string> HandedBackAsync(WeirTestServer server, long library, string relativePath, int daysAgo)
    {
        var copy = System.IO.Path.Join(server.Home, "out", relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(copy)!);
        await File.WriteAllTextAsync(copy, "cleaned film " + relativePath);
        var uow = await UnitOfWork.OpenAsync(server.Services.GetRequiredService<SqliteDatabase>());
        await using (uow)
        {
            await server.Services.GetRequiredService<HandbackStore>().RecordWrittenAsync(uow, library, relativePath, copy, DateTimeOffset.UtcNow.AddDays(-daysAgo));
            await uow.CommitAsync();
        }

        return copy;
    }

    private static Task RunCleanupAsync(UnclaimedHandbackCleanupHandler handler) =>
        handler.HandleAsync(
            new JobWorkContext(1, PeriodicJobKinds.UnclaimedHandbackCleanup, "{\"media_scope\": \"movie\", \"trigger\": \"scheduled\"}", "test"),
            CancellationToken.None);
}
