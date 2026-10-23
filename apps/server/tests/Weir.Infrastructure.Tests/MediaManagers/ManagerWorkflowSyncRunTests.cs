using System.Net;
using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// How <see cref="ManagerWorkflowSync"/> runs: a burst of requests, a run that fails, a manager kind with no port, and the libraries
/// whose folders the manager does not say yet, which a workflow is made or adopted for all the same.
/// </summary>
public sealed class ManagerWorkflowSyncRunTests : IDisposable
{
    private const string Manifest = "/api/integrations/external/manifest";
    private const string Destinations = "/api/integrations/processors/download-destinations";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static string ManifestJson(string output = "/data/ready/movies") =>
        $$"""{"libraries":[{"id":"lib-movies","name":"Movies","mediaType":"movie","rootPath":"/data/library/lib-movies","importWorkflow":"refine-before-import","processorOutputPath":"{{output}}","downloadsPath":""}]}""";

    private void DelunoSaysNoDownloadsFolder(string output = "/data/ready/movies") =>
        _fixture.Http
            .Json(HttpMethod.Get, Manifest, ManifestJson(output))
            .Json(HttpMethod.Get, Destinations, "{}", HttpStatusCode.NotFound);

    private Task<long> ConnectAsync() => _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");

    private Task SyncAsync() => _fixture.WorkflowSync.SyncAllAsync(ManagerWorkflowSync.ScheduledTrigger, CancellationToken.None);

    private Task<List<ProcessingLibraryRecord>> WorkflowsAsync() => _fixture.Db(uow => _fixture.Libraries.ListAsync(uow));

    private Task<List<string>> NoticesAsync() =>
        _fixture.Db(uow => uow.QueryAsync(
            "SELECT title FROM activity_events WHERE event_type = $type ORDER BY id", reader => reader.GetString(0), ("$type", ActivityEventTypes.ProcessingWorkflowSyncNotice)));

    private ManagerWorkflowSync SyncWith(MediaManagerConnectionService connections, ILogger<ManagerWorkflowSync> log) =>
        new(_fixture.Store.Database, _fixture.ConnectionStore, connections, _fixture.Http, _fixture.Libraries, new ScanSettingsChanges(), _fixture.Store.Options, _fixture.Store.Clock, log);

    [Fact]
    public async Task Requests_that_arrive_while_a_sync_runs_are_folded_into_one_more_run()
    {
        DelunoSaysNoDownloadsFolder();
        var firstInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var manifestCalls = 0;
        _fixture.Http.Route(HttpMethod.Get, Manifest, _ =>
        {
            switch (Interlocked.Increment(ref manifestCalls))
            {
                case 1:
                    firstInside.SetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "The first sync was never let go.");
                    break;
                case 2:
                    secondInside.SetResult();
                    break;
            }

            return FakeManagerHttp.Response(HttpStatusCode.OK, ManifestJson());
        });
        await ConnectAsync();

        try
        {
            _fixture.WorkflowSync.RequestSync();
            await firstInside.Task;
            _fixture.WorkflowSync.RequestSync();
            _fixture.WorkflowSync.RequestSync();
        }
        finally
        {
            release.Set();
        }

        await secondInside.Task;
        await SyncAsync();

        Assert.Equal(3, Volatile.Read(ref manifestCalls));
    }

    [Fact]
    public async Task A_sync_that_fails_is_logged_and_the_next_request_runs_again()
    {
        DelunoSaysNoDownloadsFolder();
        await ConnectAsync();
        await _fixture.Store.Execute("ALTER TABLE libraries RENAME TO libraries_unavailable");
        var log = new CapturingLogger<ManagerWorkflowSync>();
        using var sync = SyncWith(_fixture.Connections, log);

        sync.RequestSync();
        await Eventually.ThatAsync(() => log.Messages.Count(message => message.Level == LogLevel.Warning) == 1);
        sync.RequestSync();
        await Eventually.ThatAsync(() => log.Messages.Count(message => message.Level == LogLevel.Warning) == 2);

        Assert.All(
            log.Messages.Where(message => message.Level == LogLevel.Warning),
            message => Assert.Contains("could not set up its workflows from a media manager", message.Message, StringComparison.Ordinal));
    }

    private sealed class NoPorts : IMediaManagerPorts
    {
        public IMediaManagerPort? PortForKind(string? kind) => null;
    }

    [Fact]
    public async Task A_connection_whose_kind_has_no_port_is_never_asked()
    {
        DelunoSaysNoDownloadsFolder();
        await ConnectAsync();
        var connections = new MediaManagerConnectionService(_fixture.Store.Options, _fixture.Cipher, new NoPorts(), _fixture.ConnectionStore);
        using var sync = SyncWith(connections, new CapturingLogger<ManagerWorkflowSync>());

        await sync.SyncAllAsync(ManagerWorkflowSync.ScheduledTrigger, CancellationToken.None);

        Assert.Empty(_fixture.Http.Requests);
    }

    [Fact]
    public async Task A_library_with_no_folders_at_all_is_adopted_by_the_unconfigured_workflow_and_the_person_is_told_what_to_set()
    {
        DelunoSaysNoDownloadsFolder(output: string.Empty);
        var connection = await ConnectAsync();

        await SyncAsync();

        var movies = Assert.Single(await WorkflowsAsync(), workflow => workflow.DiscoveredLibraryKey == "lib-movies");
        Assert.Equal((connection, string.Empty, string.Empty), (movies.DiscoveredFromConnectionId, movies.WatchedFolder, movies.OutputFolder));
        Assert.Equal(["Deluno on 192.0.2.30 has not told Weir all of the folders for Movies yet"], await NoticesAsync());
    }

    [Fact]
    public async Task A_library_with_no_folders_at_all_gets_a_workflow_of_its_own_that_is_off_until_it_has_somewhere_to_write()
    {
        DelunoSaysNoDownloadsFolder(output: string.Empty);
        await ConnectAsync();
        await _fixture.Store.Execute("DELETE FROM libraries");

        await SyncAsync();

        var movies = Assert.Single(await WorkflowsAsync());
        Assert.Equal(("Movies", false, 0), (movies.Name, movies.Enabled, movies.DisplayOrder));
        Assert.Equal(("lib-movies", string.Empty, string.Empty), (movies.DiscoveredLibraryKey, movies.WatchedFolder, movies.OutputFolder));
    }

    [Fact]
    public async Task A_library_whose_output_folder_another_workflow_holds_gets_no_workflow_and_is_reported_once_as_not_set_up()
    {
        DelunoSaysNoDownloadsFolder();
        await ConnectAsync();
        await _fixture.Store.Execute("UPDATE libraries SET watched_folder = '/mine/in', output_folder = '/data/ready/movies' WHERE media_type = 'movie'");

        await SyncAsync();
        await SyncAsync();

        Assert.DoesNotContain(await WorkflowsAsync(), workflow => workflow.DiscoveredLibraryKey is not null);
        Assert.Equal(["Weir could not set up Movies from Deluno on 192.0.2.30"], await NoticesAsync());
    }

    private static string DestinationsJson(string saveFolder) =>
        $$$"""{"libraries":[{"libraryId":"lib-movies","libraryName":"Movies","destinations":[{"downloadClientName":"qBittorrent","category":"weir","categoryKind":"category","saveFolder":"{{{saveFolder}}}","savedBy":"client-category","status":"ok","message":""}],"processorConnection":{"pathMappings":[]}}]}""";

    [Fact]
    public async Task A_new_downloads_folder_another_workflow_holds_is_not_applied_and_the_person_is_told_it_was_an_update()
    {
        _fixture.Http
            .Json(HttpMethod.Get, Manifest, ManifestJson())
            .Json(HttpMethod.Get, Destinations, DestinationsJson("/data/completed/movie"));
        await ConnectAsync();
        await SyncAsync();
        await _fixture.Store.Execute(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, display_order) VALUES ('Mine', 'movie', '/data/new/movies', '/mine/out', 10)");
        _fixture.Http.Json(HttpMethod.Get, Destinations, DestinationsJson("/data/new/movies"));

        await SyncAsync();

        var movies = Assert.Single(await WorkflowsAsync(), workflow => workflow.DiscoveredLibraryKey == "lib-movies");
        Assert.Equal("/data/completed/movie", movies.WatchedFolder);
        Assert.Equal(["Weir could not update Movies from Deluno on 192.0.2.30"], await NoticesAsync());
    }

    [Fact]
    public async Task A_workflow_unlinked_from_its_manager_is_not_told_about_the_folders_the_manager_has_not_given()
    {
        DelunoSaysNoDownloadsFolder();
        var connection = await ConnectAsync();
        await SyncAsync();
        var movies = Assert.Single(await WorkflowsAsync(), workflow => workflow.DiscoveredLibraryKey == "lib-movies");
        await _fixture.Store.Execute("DELETE FROM activity_events");
        await _fixture.Store.Execute($"DELETE FROM library_manager_links WHERE library_id = {movies.Id} AND connection_id = {connection}");

        await SyncAsync();

        Assert.Empty(await NoticesAsync());
    }
}
