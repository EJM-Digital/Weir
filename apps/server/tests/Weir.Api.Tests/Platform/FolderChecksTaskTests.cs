using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The server looks at each workflow's folders on its own while a browser watches, and says so when an answer is not the one it
/// gave last time: a folder that goes missing or comes back is pushed, never waited for.
/// </summary>
public sealed class FolderChecksTaskTests : IDisposable
{
    private const string Sentinel = "sentinel";
    private readonly string _root = Directory.CreateTempSubdirectory("weir-folder-checks-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed record Rig(
        WeirTestServer Server, FolderChecksTask Task, DataChangePublisher Changes, ActivityStreamClients Clients, ServerLooks Looks, string WatchedFolder,
        ApiTestClient Client, long LibraryId);

    private async Task<Rig> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var watched = Directory.CreateDirectory(Path.Join(_root, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(_root, "output")).FullName;
        using var created = await client.PostAsync(
            "/api/v1/processing/libraries",
            new
            {
                csrf_token = await client.CsrfAsync(),
                name = "Checked",
                media_type = "movie",
                watched_folder = watched,
                output_folder = output,
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var libraryId = (long)(await ApiTestClient.Json(created))["id"]!;

        // The task is the server's own, built over its parts with a count of watchers of its own, so
        // the server's timer cannot add a look the test did not ask for.
        var changes = server.Services.GetRequiredService<DataChangePublisher>();
        var clients = new ActivityStreamClients();
        var looks = new ServerLooks(TimeProvider.System);
        var task = new FolderChecksTask(
            server.Services.GetRequiredService<SqliteDatabase>(),
            server.Services.GetRequiredService<LibraryStore>(),
            server.Services.GetRequiredService<LibraryFolderChainCheck>(),
            clients,
            looks);
        return new Rig(server, task, changes, clients, looks, watched, client, libraryId);
    }

    private static async Task<string> NextAsync(BroadcastSubscription<string> subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var topic in subscription.ReadAllAsync(timeout.Token))
        {
            return topic;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    /// <summary>Whether the folder checks were published since the last call: the topics up to a marker are read, and the server may publish others between them.</summary>
    private static async Task<bool> PublishedAsync(Rig rig, BroadcastSubscription<string> subscription)
    {
        rig.Changes.Publish(Sentinel);
        var published = false;
        string topic;
        while ((topic = await NextAsync(subscription)) != Sentinel)
        {
            published |= topic == DataTopics.FolderChecks;
        }

        return published;
    }

    [Fact]
    public async Task A_folder_that_goes_missing_and_comes_back_is_published_each_time_the_answer_changes()
    {
        var rig = await StartAsync();
        await using var _server = rig.Server;
        using var watcher = rig.Clients.Open();
        using var subscription = rig.Changes.Subscribe();

        await rig.Task.RunOnceAsync(CancellationToken.None);
        Assert.True(await PublishedAsync(rig, subscription));

        await rig.Task.RunOnceAsync(CancellationToken.None);
        Assert.False(await PublishedAsync(rig, subscription), "An answer that has not changed is not published again.");

        Directory.Delete(rig.WatchedFolder);
        await rig.Task.RunOnceAsync(CancellationToken.None);
        Assert.True(await PublishedAsync(rig, subscription));

        Directory.CreateDirectory(rig.WatchedFolder);
        await rig.Task.RunOnceAsync(CancellationToken.None);
        Assert.True(await PublishedAsync(rig, subscription));
    }

    [Fact]
    public async Task A_folder_a_screen_found_gone_is_published_so_its_coming_back_is_published_too()
    {
        var rig = await StartAsync();
        await using var _server = rig.Server;
        using var watcher = rig.Clients.Open();
        using var subscription = rig.Changes.Subscribe();
        await rig.Task.RunOnceAsync(CancellationToken.None);
        Assert.True(await PublishedAsync(rig, subscription));

        // A screen reads the workflow on its own (it was opened, it came back to the foreground, its live link was lost and found
        // again) between two of the server's looks, and finds the folder gone.
        Directory.Delete(rig.WatchedFolder);
        using var read = await rig.Client.GetAsync($"/api/v1/processing/libraries/{rig.LibraryId}/folder-chain");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(await PublishedAsync(rig, subscription), "Every other screen must be told what the read found.");

        // The folder is back before the server looks again, so the server never sees it gone. The screen that read it gone has to be told.
        Directory.CreateDirectory(rig.WatchedFolder);
        await rig.Task.RunOnceAsync(CancellationToken.None);
        Assert.True(await PublishedAsync(rig, subscription), "The screen that read the folder gone must be told it is back.");
    }

    [Fact]
    public async Task Every_look_is_told_to_the_streams_even_when_the_answer_has_not_changed()
    {
        var rig = await StartAsync();
        await using var _server = rig.Server;
        using var watcher = rig.Clients.Open();
        using var looks = rig.Looks.Subscribe();

        await rig.Task.RunOnceAsync(CancellationToken.None);
        var first = rig.Looks.Latest.Folders;
        await rig.Task.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.True(rig.Looks.Latest.Folders >= first);
        Assert.Equal(2, await CountAsync(looks));
    }

    private static async Task<long> CountAsync(BroadcastSubscription<long> looks)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        long last = 0;
        await foreach (var version in looks.ReadAllAsync(timeout.Token))
        {
            last = version;
            if (last == 2)
            {
                break;
            }
        }

        return last;
    }

    [Fact]
    public async Task With_nobody_watching_nothing_is_checked_or_published()
    {
        var rig = await StartAsync();
        await using var _server = rig.Server;
        using var subscription = rig.Changes.Subscribe();

        await rig.Task.RunOnceAsync(CancellationToken.None);

        Assert.False(await PublishedAsync(rig, subscription));
        Assert.Null(rig.Looks.Latest.Folders);
    }

    [Fact]
    public async Task A_workflow_that_is_switched_off_is_not_looked_at()
    {
        var rig = await StartAsync();
        await using var _server = rig.Server;
        await TestDatabase.ScalarAsync(rig.Server, "UPDATE libraries SET enabled = 0 RETURNING 1");
        using var watcher = rig.Clients.Open();
        using var subscription = rig.Changes.Subscribe();

        await rig.Task.RunOnceAsync(CancellationToken.None);

        Assert.False(await PublishedAsync(rig, subscription));
    }
}
