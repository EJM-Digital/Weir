using System.Text.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>
/// Setting up workflows from what a connected Deluno says it manages, against a scripted manifest and real folders: what is offered,
/// what is imported and with what folders, and what has drifted since. Nothing here changes a workflow the person already has.
/// </summary>
public sealed class LibraryDiscoveryServiceTests : IDisposable
{
    private const string Manifest = "/api/integrations/external/manifest";

    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private LibraryDiscoveryService Service() => new(_fixture.Connections, _fixture.Ports, _fixture.Libraries);

    private sealed class NoPorts : IMediaManagerPorts
    {
        public IMediaManagerPort? PortForKind(string? kind) => null;
    }

    private string Folder(string name)
    {
        var path = _fixture.Store.Home.Join(name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void Manifested(params Dictionary<string, object?>[] libraries) =>
        _fixture.Http.Json(HttpMethod.Get, Manifest, JsonSerializer.Serialize(new { libraries }));

    private static Dictionary<string, object?> Library(
        string id, string name, string mediaType, string? root, string workflow = "import-only", string? output = null, string? downloads = null) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["mediaType"] = mediaType,
        ["rootPath"] = root,
        ["importWorkflow"] = workflow,
        ["processorOutputPath"] = output,
        ["downloadsPath"] = downloads,
    };

    private async Task<MediaManagerConnectionRecord> DelunoAsync(string baseUrl = "http://deluno.local", string? apiKey = "key")
    {
        var id = await _fixture.AddConnectionAsync("deluno", baseUrl, apiKey);
        return (await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, id)))!;
    }

    private Task<T> Db<T>(Func<Infrastructure.Sqlite.UnitOfWork, Task<T>> work) => _fixture.Db(work);

    [Fact]
    public void The_service_cannot_be_built_without_its_parts()
    {
        Assert.Throws<ArgumentNullException>(() => new LibraryDiscoveryService(null!, _fixture.Ports, _fixture.Libraries));
        Assert.Throws<ArgumentNullException>(() => new LibraryDiscoveryService(_fixture.Connections, null!, _fixture.Libraries));
        Assert.Throws<ArgumentNullException>(() => new LibraryDiscoveryService(_fixture.Connections, _fixture.Ports, null!));
    }

    [Fact]
    public async Task Every_entry_point_refuses_a_missing_argument()
    {
        var service = Service();
        var connection = await DelunoAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.DiscoverableLibrariesAsync(null!, connection));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Db(uow => service.DiscoverableLibrariesAsync(uow, null!)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ImportLibrariesAsync(null!, connection, ["a"]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Db(uow => service.ImportLibrariesAsync(uow, null!, ["a"])));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Db(uow => service.ImportLibrariesAsync(uow, connection, null!)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ResyncDriftAsync(null!, connection));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Db(uow => service.ResyncDriftAsync(uow, null!)));
        Assert.Throws<ArgumentNullException>(() => LibraryDiscoveryService.WatchedSource(null!));
    }

    [Fact]
    public void A_manager_that_processes_first_is_watched_where_its_downloads_arrive_and_any_other_where_its_library_is()
    {
        var processing = new ManagerLibraryDescriptor("1", "Movies", "movie", "/lib", "/out", true, "/downloads");
        var noDownloads = processing with { DownloadsPath = " " };
        var importOnly = processing with { ProcessesBeforeImport = false };

        Assert.Equal("/downloads", LibraryDiscoveryService.WatchedSource(processing));
        Assert.Equal("/lib", LibraryDiscoveryService.WatchedSource(noDownloads));
        Assert.Equal("/lib", LibraryDiscoveryService.WatchedSource(importOnly));
    }

    [Fact]
    public void A_path_Weir_cannot_use_says_why_in_plain_words()
    {
        var real = Folder("real");
        var missing = Path.Join(_fixture.Store.Home.Path, "not-there");

        Assert.Contains("did not say where", LibraryDiscoveryService.LocalPathProblem(null), StringComparison.Ordinal);
        Assert.Contains("did not say where", LibraryDiscoveryService.LocalPathProblem("  "), StringComparison.Ordinal);
        Assert.Contains("not an absolute path", LibraryDiscoveryService.LocalPathProblem("movies/here"), StringComparison.Ordinal);
        Assert.Contains("does not exist on the machine running Weir", LibraryDiscoveryService.LocalPathProblem(missing), StringComparison.Ordinal);
        Assert.Null(LibraryDiscoveryService.LocalPathProblem(real));
    }

    [Fact]
    public void A_name_already_taken_gets_the_first_free_number_and_a_crowd_of_them_is_refused()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "Movies" };

        Assert.Equal("Films", LibraryDiscoveryService.UniqueName(taken, "Films"));
        Assert.Equal("Movies (2)", LibraryDiscoveryService.UniqueName(taken, "Movies"));
        taken.Add("Movies (2)");
        Assert.Equal("Movies (3)", LibraryDiscoveryService.UniqueName(taken, "Movies"));
        for (var suffix = 3; suffix < 100; suffix++)
        {
            taken.Add($"Movies ({suffix})");
        }

        var refusal = Assert.Throws<ProcessingDiscoveryException>(() => LibraryDiscoveryService.UniqueName(taken, "Movies"));
        Assert.Contains("Too many workflows", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_with_no_saved_key_cannot_be_asked_anything()
    {
        var connection = await DelunoAsync(apiKey: null);

        var refusal = await Assert.ThrowsAsync<ProcessingDiscoveryException>(() => Db(uow => Service().DiscoverableLibrariesAsync(uow, connection)));

        Assert.Contains("no saved address and API key", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_Weir_cannot_talk_to_is_said_so()
    {
        var connection = await DelunoAsync();
        var service = new LibraryDiscoveryService(_fixture.Connections, new NoPorts(), _fixture.Libraries);

        var refusal = await Assert.ThrowsAsync<ProcessingDiscoveryException>(() => Db(uow => service.DiscoverableLibrariesAsync(uow, connection)));

        Assert.Contains("does not know how to talk to a deluno", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_that_does_not_answer_gives_the_reason_it_gave_or_a_plain_sentence()
    {
        var connection = await DelunoAsync();
        _fixture.Http.Json(HttpMethod.Get, Manifest, "{}", System.Net.HttpStatusCode.InternalServerError);

        var refusal = await Assert.ThrowsAsync<ProcessingDiscoveryException>(() => Db(uow => Service().DiscoverableLibrariesAsync(uow, connection)));

        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
    }

    [Fact]
    public async Task What_the_manager_reports_is_offered_with_what_Weir_already_has_and_what_Weir_cannot_see()
    {
        var downloads = Folder("downloads");
        var out1 = Folder("ready-movies");
        var connection = await DelunoAsync();
        Manifested(
            Library("m", "Movies", "movie", "/nowhere/movies", "refine-before-import", output: out1, downloads: downloads),
            Library("t", "Shows", "tv", Folder("shows")),
            Library("odd", "Odd", "music", "relative"));
        await Db(uow => Service().ImportLibrariesAsync(uow, connection, ["t"]));

        var offered = await Db(uow => Service().DiscoverableLibrariesAsync(uow, connection));

        Assert.Equal(["m", "t", "odd"], offered.Select(library => library.Key));
        var movies = offered[0];
        Assert.Equal(downloads, movies.RootPath);
        Assert.Equal("movie", movies.MediaType);
        Assert.False(movies.AlreadyImported);
        Assert.Null(movies.LocalPathProblem);
        Assert.True(movies.ProcessesBeforeImport);
        Assert.Equal(out1, movies.OutputPath);
        Assert.Null(movies.OutputPathProblem);
        Assert.True(offered[1].AlreadyImported);
        Assert.Null(offered[1].OutputPathProblem);
        Assert.Contains("not an absolute path", offered[2].LocalPathProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_a_processing_manager_hands_back_to_that_Weir_cannot_see_is_named_before_the_import()
    {
        var connection = await DelunoAsync();
        Manifested(Library("m", "Movies", "movie", Folder("lib"), "refine-before-import", output: "/nowhere/ready", downloads: Folder("dl")));

        var offered = await Db(uow => Service().DiscoverableLibrariesAsync(uow, connection));

        Assert.Contains("does not exist", offered[0].OutputPathProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_nothing_to_import_is_refused()
    {
        var connection = await DelunoAsync();

        var refusal = await Assert.ThrowsAsync<ProcessingDiscoveryException>(() => Db(uow => Service().ImportLibrariesAsync(uow, connection, ["", ""])));

        Assert.Contains("Choose at least one", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_the_manager_no_longer_reports_stops_the_whole_import()
    {
        var connection = await DelunoAsync();
        Manifested(Library("m", "Movies", "movie", Folder("lib")));

        var refusal = await Assert.ThrowsAsync<ProcessingDiscoveryException>(() => Db(uow => Service().ImportLibrariesAsync(uow, connection, ["m", "gone"])));

        Assert.Contains("no longer reports a library with id gone", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM libraries WHERE discovered_library_key IS NOT NULL"));
    }

    [Fact]
    public async Task An_imported_workflow_watches_what_Weir_can_see_and_is_on_only_when_it_has_somewhere_to_hand_back()
    {
        var downloads = Folder("downloads");
        var ready = Folder("ready");
        var connection = await DelunoAsync();
        Manifested(
            Library("m", "Movies", "movie", Folder("lib"), "refine-before-import", output: ready, downloads: downloads),
            Library("t", "Shows", "tv", Folder("shows")),
            Library("x", "Unseen", "movie", "/nowhere/lib", "refine-before-import", output: "/nowhere/ready"));

        var created = await Db(uow => Service().ImportLibrariesAsync(uow, connection, ["m", "t", "x"]));

        Assert.Equal(["Movies (2)", "Shows", "Unseen"], created.Select(row => row.Name));
        Assert.Equal(downloads, created[0].WatchedFolder);
        Assert.Equal(ready, created[0].OutputFolder);
        Assert.True(created[0].Enabled);
        Assert.Equal(connection.Id, created[0].DiscoveredFromConnectionId);
        Assert.Equal("m", created[0].DiscoveredLibraryKey);
        Assert.Equal("tv", created[1].MediaType);
        Assert.False(created[1].Enabled);
        Assert.Equal(string.Empty, created[1].OutputFolder);
        Assert.Equal(string.Empty, created[2].WatchedFolder);
        Assert.Equal(string.Empty, created[2].OutputFolder);
        Assert.False(created[2].Enabled);
        Assert.True(created[1].DisplayOrder > 0 && created[2].DisplayOrder > created[1].DisplayOrder);
    }

    [Fact]
    public async Task A_library_of_a_kind_Weir_does_not_know_is_imported_as_movies_and_a_taken_name_is_numbered()
    {
        var connection = await DelunoAsync();
        Manifested(Library("a", "Movies", "music", Folder("lib")));

        var created = await Db(uow => Service().ImportLibrariesAsync(uow, connection, ["a"]));

        Assert.Equal("movie", created[0].MediaType);
        Assert.Equal("Movies (2)", created[0].Name);
    }

    [Fact]
    public async Task Nothing_the_manager_says_changes_a_workflow_but_what_has_drifted_is_reported()
    {
        var kept = Folder("kept");
        var moved = Folder("moved");
        var connection = await DelunoAsync();
        Manifested(
            Library("same", "Same", "movie", kept),
            Library("moves", "Moves", "movie", moved),
            Library("gone", "Gone", "movie", Folder("gone")));
        var created = await Db(uow => Service().ImportLibrariesAsync(uow, connection, ["same", "moves", "gone"]));
        await _fixture.Store.Execute($"UPDATE libraries SET watched_folder = '{Path.Join(_fixture.Store.Home.Path, "elsewhere").Replace("'", "''", StringComparison.Ordinal)}' WHERE id = {created[1].Id}");
        Manifested(
            Library("same", "Same", "movie", kept),
            Library("moves", "Moves", "movie", moved),
            Library("new", "Brand new", "tv", Folder("new")));

        var drift = await Db(uow => Service().ResyncDriftAsync(uow, connection));

        Assert.Equal(
            [LibraryDriftKinds.RootMoved, LibraryDriftKinds.LibraryRemoved, LibraryDriftKinds.LibraryAdded],
            drift.Select(item => item.Kind));
        var rootMoved = drift[0];
        Assert.Equal(created[1].Id, rootMoved.LibraryId);
        Assert.Equal(moved, rootMoved.ManagerValue);
        Assert.Contains("Nothing has been changed", rootMoved.Detail, StringComparison.Ordinal);
        Assert.Equal(created[2].Id, drift[1].LibraryId);
        Assert.Equal(created[2].WatchedFolder, drift[1].WeirValue);
        Assert.Null(drift[2].LibraryId);
        Assert.Equal("Brand new", drift[2].LibraryName);
        var after = await Db(uow => _fixture.Libraries.GetAsync(uow, created[1].Id));
        Assert.Contains("elsewhere", after!.WatchedFolder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removed_library_that_never_had_a_folder_has_nothing_to_show_for_Weir_s_side()
    {
        var connection = await DelunoAsync();
        Manifested(Library("gone", "Gone", "movie", "/nowhere/lib"));
        var created = await Db(uow => Service().ImportLibrariesAsync(uow, connection, ["gone"]));
        Manifested();

        var drift = await Db(uow => Service().ResyncDriftAsync(uow, connection));

        Assert.Equal(LibraryDriftKinds.LibraryRemoved, drift.Single().Kind);
        Assert.Equal(created[0].Id, drift.Single().LibraryId);
        Assert.Null(drift.Single().WeirValue);
    }

    [Fact]
    public async Task A_workflow_with_no_folder_whose_manager_folder_Weir_cannot_see_is_reported_as_not_local()
    {
        var connection = await DelunoAsync();
        Manifested(Library("x", "Unseen", "movie", "/nowhere/lib"), Library("blank", "Blank", "movie", null));
        await Db(uow => Service().ImportLibrariesAsync(uow, connection, ["x", "blank"]));

        var drift = await Db(uow => Service().ResyncDriftAsync(uow, connection));

        Assert.Equal([LibraryDriftKinds.PathNotLocal, LibraryDriftKinds.PathNotLocal], drift.Select(item => item.Kind));
        Assert.Equal("/nowhere/lib", drift[0].ManagerValue);
        Assert.Null(drift[1].ManagerValue);
    }

    [Fact]
    public async Task A_folder_spelled_differently_but_the_same_is_not_drift()
    {
        var folder = Folder("lib");
        var connection = await DelunoAsync();
        Manifested(Library("m", "Movies", "movie", folder));
        await Db(uow => Service().ImportLibrariesAsync(uow, connection, ["m"]));
        Manifested(Library("m", "Movies", "movie", folder + Path.DirectorySeparatorChar));

        var drift = await Db(uow => Service().ResyncDriftAsync(uow, connection));

        Assert.Empty(drift);
    }

    [Fact]
    public async Task Forgetting_where_a_workflow_came_from_leaves_the_workflow_as_it_is()
    {
        var folder = Folder("lib");
        var connection = await DelunoAsync();
        Manifested(Library("m", "Movies", "movie", folder));
        var created = await Db(uow => Service().ImportLibrariesAsync(uow, connection, ["m"]));

        var unlinked = await Db(uow => Service().UnlinkLibraryAsync(uow, created[0]));

        Assert.Null(unlinked.DiscoveredLibraryKey);
        Assert.Null(unlinked.DiscoveredFromConnectionId);
        Assert.Equal(folder, unlinked.WatchedFolder);
    }
}
