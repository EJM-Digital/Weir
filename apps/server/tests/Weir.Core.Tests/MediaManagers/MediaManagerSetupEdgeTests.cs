using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// The last edges of the folder checks and workflow sync: blank folders, a client with no mapping, a manager that names nothing, and
/// folders that share a parent or do not.
/// </summary>
public sealed class MediaManagerSetupEdgeTests
{
    private static WireValue Json(string json) => WireJsonParser.Parse(json);

    private sealed class Probe(string? finalFolder = null) : IFolderProbe
    {
        public bool Exists(string path) => true;

        public bool CanRead(string path) => true;

        public bool CanWrite(string path) => true;

        public string? ResolveFinalPath(string path) => finalFolder ?? path;

        public bool? SameFilesystem(string first, string second) => true;
    }

    [Theory]
    [InlineData("", "/work", "/out", "Set a watched folder")]
    [InlineData("/watched", "", "/out", "Set a work folder")]
    [InlineData("/watched", "/work", "", "Set an output folder")]
    [InlineData("", "", "/out", "Set this workflow's watched, work and output folders")]
    [InlineData("  ", "/work", " ", "Set this workflow's watched, work and output folders")]
    public void A_blank_folder_is_named_when_there_is_one_and_all_of_them_when_there_are_several(string watched, string work, string output, string expected)
    {
        var lines = LibraryFolderChainRules.CheckLocalFolders(watched, work, false, output, new Probe());

        Assert.Contains(expected, Assert.Single(lines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Folders_to_reach_leave_out_the_blank_ones()
    {
        var folders = LibraryFolderChainRules.FoldersToReach(" /watched ", "", true, "/out");

        Assert.Equal(["/watched", "/out"], folders.Select(folder => folder.Path));
    }

    private static readonly ArrDownloadClientEntry Client = new("qBittorrent", "QBittorrent", true, "qb", "tv", null, "usenet");

    [Fact]
    public void A_download_client_whose_host_has_no_mapping_is_named_when_another_host_has_one()
    {
        var mappings = new[] { new RemotePathMappingEntry("qb", "/downloads/complete", "/data/weir") };
        var clients = new[] { Client, Client with { Name = "Other", Host = "other" } };

        var result = ManagerSetupRules.EvaluateArr("Sonarr", MediaManagerKinds.Tv, "/downloads/complete", "/data/weir", mappings, clients, completedDownloadHandling: null);

        Assert.Contains(result.Lines, line => line.State == SetupCheckLine.Problem && line.Text.Contains("The download client at \"other\" has no mapping", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, line => line.Text.StartsWith("Completed Download Handling", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manager_with_no_mapping_at_all_is_told_to_add_one()
    {
        var result = ManagerSetupRules.EvaluateArr("Sonarr", MediaManagerKinds.Tv, "/downloads/complete", "/data/weir", [], [Client], completedDownloadHandling: true);

        Assert.Contains(result.Lines, line => line.Text.Contains("has no remote path mapping for /downloads/complete yet", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.Text == "Completed Download Handling is on in Sonarr.");
    }

    [Fact]
    public void Download_client_rows_may_leave_out_enable_and_the_implementation()
    {
        var clients = ManagerSetupRules.ParseDownloadClients(
            Json("""[{"name": "Bare", "fields": [{"name": "host", "value": "h"}, {"name": "tvDirectory", "value": "/tv"}]}]"""),
            MediaManagerKinds.Tv);

        var client = Assert.Single(clients);
        Assert.True(client.Enabled);
        Assert.Equal(string.Empty, client.Implementation);
        Assert.Equal("/tv", client.Directory);
    }

    private static readonly ManagerLibraryDescriptor RefiningMovies =
        new("lib-movies", "Movies", MediaManagerKinds.Movie, "C:\\Media\\Movies", "C:\\Weir\\Ready\\Movies", true, "C:\\NasMount\\Completed\\Movies");

    [Theory]
    [InlineData(MediaManagerKinds.Movie, "movies-cat", "the category \"movies-cat\"")]
    [InlineData(MediaManagerKinds.Movie, null, "its downloads")]
    [InlineData(MediaManagerKinds.Tv, "tv-cat", "the category \"tv-cat\"")]
    [InlineData(MediaManagerKinds.Tv, null, "its downloads")]
    public void A_download_client_is_described_by_the_category_for_the_media_type_when_it_has_one(string scope, string? category, string expected)
    {
        var library = RefiningMovies with { MediaScope = scope };
        var client = new ManagerDownloadClientDescriptor("qBittorrent", true, scope == MediaManagerKinds.Movie ? category : null, scope == MediaManagerKinds.Tv ? category : null);

        var result = ManagerSetupRules.EvaluateDeluno("Deluno", scope, "C:\\Downloads\\Movies", "C:\\Weir\\Ready\\Movies", [library], [client]);

        Assert.Contains(result.Lines, line => line.Text.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_folder_that_a_link_leads_to_nothing_is_not_taken_for_inside_the_watched_folder()
    {
        var result = ManagerSetupRules.EvaluateDeluno(
            "Deluno", MediaManagerKinds.Movie, "C:\\Elsewhere", "C:\\Weir\\Ready\\Movies", [RefiningMovies], null, new Probe(finalFolder: string.Empty));

        Assert.Contains(result.Lines, line => line.State == SetupCheckLine.Unverified && line.Text.Contains("isn't inside this workflow's watched folder", StringComparison.Ordinal));
    }

    [Fact]
    public void A_choice_with_no_library_ends_the_check_with_the_choices_own_lines()
    {
        var choice = new DelunoLibraryChoice(null, [new SetupCheckLine(SetupCheckLine.Problem, "No library.")]);

        var result = ManagerSetupRules.EvaluateDelunoWithDestinations("Deluno", MediaManagerKinds.Movie, "/w", "/o", choice, null, DelunoDestinationsAnswer.Read([]));

        Assert.Equal("No library.", Assert.Single(result.Lines).Text);
    }

    private static DelunoSetupResult Evaluate(DelunoDestinationsAnswer answer, string watched = "C:\\Downloads\\Movies", string output = "C:\\Weir\\Ready\\Movies")
    {
        var choice = ManagerSetupRules.ChooseDelunoLibrary("Deluno", MediaManagerKinds.Movie, [RefiningMovies]);
        return ManagerSetupRules.EvaluateDelunoWithDestinations("Deluno", MediaManagerKinds.Movie, watched, output, choice, null, answer);
    }

    [Fact]
    public void An_unreachable_deluno_with_no_words_of_its_own_says_it_did_not_say_where_downloads_are_saved()
    {
        var result = Evaluate(DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.Unreachable));

        Assert.Contains(result.Lines, line => line.Text == "Deluno did not say where its downloads are saved.");
    }

    private static DelunoLibraryDestinations Library(params DelunoDestination[] destinations) =>
        new("lib-movies", "Movies", "C:\\NasMount\\Completed\\Movies", "C:\\NasMount\\Ready\\Movies", destinations, [new DelunoPathMapping("C:\\NasMount", "C:\\Weir")]);

    [Theory]
    [InlineData("", "", "Deluno reports a problem with qBittorrent for its downloads.")]
    [InlineData("tv", "It is full", "Deluno reports a problem with qBittorrent for the \"tv\" category: It is full")]
    public void A_client_with_a_problem_is_named_with_its_category_and_deluno_words_when_it_has_them(string category, string message, string expected)
    {
        var destination = new DelunoDestination("qBittorrent", category, "category", null, string.Empty, DelunoDestination.Problem, message);

        var result = Evaluate(DelunoDestinationsAnswer.Read([Library(destination)]));

        Assert.Contains(result.Lines, line => line.Text == expected);
    }

    [Fact]
    public void A_clients_save_folder_is_not_judged_for_a_workflow_with_no_watched_folder()
    {
        var destination = new DelunoDestination("qBittorrent", "radarr", "category", "C:\\NasMount\\Completed\\Movies", "deluno-per-grab", DelunoDestination.Ok, string.Empty);

        var result = Evaluate(DelunoDestinationsAnswer.Read([Library(destination)]), watched: string.Empty);

        Assert.DoesNotContain(result.Lines, line => line.Text.Contains("qBittorrent saves", StringComparison.Ordinal));
    }

    [Fact]
    public void A_library_picked_up_from_a_folder_when_the_workflow_has_no_output_folder_is_told_to_set_one()
    {
        var result = Evaluate(DelunoDestinationsAnswer.Read([Library()]), output: string.Empty);

        Assert.Contains(result.Lines, line => line.State == SetupCheckLine.Problem && line.Text.Contains("has no output folder", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "", "No Weir workflow watches the folder that '' is in.")]
    [InlineData("/", "", "No Weir workflow watches the folder that '' is in.")]
    [InlineData("/a/b/Film.mkv", "Film.mkv", "No Weir workflow watches the folder that 'Film.mkv' is in.")]
    public void A_refusal_names_the_file_by_its_last_part_when_it_has_one(string? path, string unused, string expected)
    {
        Assert.NotNull(unused);
        Assert.StartsWith(expected, NoWorkflowWatches.Detail([], path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_workflow_of_a_kind_that_has_no_label_is_listed_by_its_kind()
    {
        var detail = NoWorkflowWatches.Detail([new WorkflowFolder("Kids", "cartoons", "/kids"), new WorkflowFolder("Movies", "movie", "")], "/x/film.mkv");

        Assert.Contains("Kids (cartoons) watches '/kids'", detail, StringComparison.Ordinal);
        Assert.Contains("Movies has no watched folder yet", detail, StringComparison.Ordinal);
    }

    private static DelunoLibraryDestinations Saving(params string[] folders) =>
        new("lib", "Movies", null, null, [.. folders.Select(folder => new DelunoDestination("qb", "movies", "category", folder, "deluno-per-grab", DelunoDestination.Ok, string.Empty))], []);

    [Theory]
    [InlineData(new[] { "/data/dl/a", "/data/dl/a/" }, "/data/dl/a")]
    [InlineData(new[] { "/data/dl/movies", "/data/dl/tv" }, "/data/dl")]
    [InlineData(new[] { "/data/dl/movies/x", "/data/dl/movies" }, "/data/dl/movies")]
    [InlineData(new[] { "/data/a", @"C:\Down\b" }, "/data/a")]
    [InlineData(new[] { @"C:\Down\a", @"c:\down\b" }, @"C:\Down")]
    [InlineData(new[] { @"\\nas\share\a", @"\\nas\share\b" }, @"\\nas\share\a")]
    [InlineData(new[] { @"\\nas\share\x\a", @"\\nas\share\x\b" }, @"\\nas\share\x")]
    [InlineData(new[] { @"C:\a", @"C:\b" }, @"C:\a")]
    [InlineData(new[] { "/a", "/b" }, "/a")]
    public void Folders_clients_save_to_become_one_folder_when_they_share_a_real_parent(string[] folders, string expected)
    {
        Assert.Equal(expected, WorkflowSyncFolders.Watched("Deluno", "Movies", null, Saving(folders)).Folder);
    }

    [Fact]
    public void A_library_with_no_destinations_is_kept_in_step_by_its_downloads_folder_and_the_managers_output_folder()
    {
        var library = new ManagerLibraryDescriptor("lib", "Movies", MediaManagerKinds.Movie, null, null, true, "/dl");

        var withDestinations = WorkflowSyncRules.LibraryOf("Deluno", library, new DelunoLibraryDestinations("lib", "Movies", null, "/processed", [], []));
        var withOutputOfItsOwn = WorkflowSyncRules.LibraryOf("Deluno", library with { OutputPath = "/own" }, null);
        var withNothing = WorkflowSyncRules.LibraryOf("Deluno", library with { DownloadsPath = null }, null);

        Assert.Equal(("/dl", "/processed"), (withDestinations.WatchedFolder, withDestinations.OutputFolder));
        Assert.Equal(("/dl", "/own"), (withOutputOfItsOwn.WatchedFolder, withOutputOfItsOwn.OutputFolder));
        Assert.Equal(SyncedLibraryProblem.NoDownloadsFolder, withNothing.Cause);
        Assert.Equal(SyncedLibraryProblem.NoOutputFolder, WorkflowSyncRules.LibraryOf("Deluno", library, null).Cause);
    }

    [Fact]
    public void An_update_message_names_only_the_folders_that_the_manager_gave()
    {
        Assert.Equal("Deluno now reports /dl for downloads for Movies, so Weir changed it to match.", WorkflowSyncRules.UpdatedMessage("Movies", "Deluno", "/dl", null));
        Assert.Equal("Deluno now reports /out for cleaned files for Movies, so Weir changed it to match.", WorkflowSyncRules.UpdatedMessage("Movies", "Deluno", null, "/out"));
        Assert.Contains("watches no folder yet and writes to no folder yet", WorkflowSyncRules.SetUpMessage("Movies", "Deluno", null, null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"result": {"apply_move_completed": true, "move_completed_path": "/done/tv"}, "error": null}""")]
    public void A_deluge_label_that_applies_a_folder_but_does_not_say_to_move_has_none(string options)
    {
        Assert.Empty(DelugeRules.Parse(null, [("tv", Json(options))]).CategoryFolders);
    }

    [Fact]
    public void A_destination_with_every_field_given_is_read_as_written()
    {
        var libraries = DelunoDestinationRules.ParseLibraries(Json("""
            {"libraries": [{"libraryId": "lib", "libraryName": "Movies", "destinations": [
              {"downloadClientName": "qBittorrent", "category": "radarr", "categoryKind": "label", "saveFolder": "/dl", "savedBy": "client-category", "status": "OK", "message": "fine"}]}]}
            """));

        var destination = Assert.Single(Assert.Single(libraries).Destinations);
        Assert.Equal(("qBittorrent", "radarr", "label", "/dl", "client-category", DelunoDestination.Ok, "fine"),
            (destination.ClientName, destination.Category, destination.CategoryKind, destination.SaveFolder, destination.SavedBy, destination.Status, destination.Message));
    }

    [Theory]
    [InlineData("/x/Movies", "xMovies")]
    [InlineData("x/Movies", "xMovies")]
    public void Path_mapping_advice_compares_a_separator_with_a_letter(string deluno, string weir)
    {
        Assert.Contains("Add a path mapping from", DelunoPathMappingAdvice.WhenNoMappingCovers("Deluno", [new DelunoPathMapping("/a", "/b")], deluno, weir, "the watched folder"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_manager_path_is_compared_in_the_way_the_host_compares_paths()
    {
        Assert.True(HandbackRules.ManagerPathEndsWith("/Data/Media/Film.mkv", ["media", "film.mkv"], hostIsWindows: true));
        Assert.False(HandbackRules.ManagerPathEndsWith("/Data/Media/Film.mkv", ["media", "film.mkv"], hostIsWindows: false));
        Assert.Equal(OperatingSystem.IsWindows(), HandbackRules.ManagerPathEndsWith("/Data/Media/Film.mkv", ["media", "film.mkv"]));
        Assert.True(HandbackRules.ManagerPathEndsWith(@"D:\Data\Media\Film.mkv", ["media", "film.mkv"]));
        Assert.Equal("film.mkv", HandbackRules.FileName("/data/film.mkv"));
        Assert.Equal(string.Empty, HandbackRules.FileName("///"));
    }

    [Fact]
    public void A_destination_with_no_client_name_is_called_its_download_client()
    {
        var libraries = DelunoDestinationRules.ParseLibraries(Json("""{"libraries": [{"libraryId": "lib", "destinations": [{"category": "radarr"}]}]}"""));

        Assert.Equal("its download client", Assert.Single(Assert.Single(libraries).Destinations).ClientName);
    }

    [Theory]
    [InlineData(1, "A download in Sonarr's queue already points at Weir's output folder.")]
    [InlineData(2, "2 downloads in Sonarr's queue already point at Weir's output folder.")]
    public void Downloads_in_the_queue_that_already_point_at_the_output_folder_are_counted(int count, string expected)
    {
        var mappings = new[] { new RemotePathMappingEntry("qb", "/downloads/complete", "/data/weir") };
        string[] queued = ["/data/weir/a.mkv", "/data/weir/b.mkv"];

        var result = ManagerSetupRules.EvaluateArr("Sonarr", MediaManagerKinds.Tv, "/downloads/complete", "/data/weir", mappings, [Client], true, queued.Take(count).ToList());

        Assert.Contains(result.Lines, line => line.Text == expected);
    }

    [Fact]
    public void A_mapping_for_another_host_than_the_download_clients_is_named_with_both_hosts()
    {
        var mappings = new[] { new RemotePathMappingEntry("elsewhere", "/downloads/complete", "/data/weir") };

        var result = ManagerSetupRules.EvaluateArr("Sonarr", MediaManagerKinds.Tv, "/downloads/complete", "/data/weir", mappings, [Client], true);

        Assert.Contains(result.Lines, line => line.Text.Contains("for the host \"elsewhere\", but its download client's Host is", StringComparison.Ordinal));
    }

    [Fact]
    public void A_workflow_of_a_kind_with_no_name_is_listed_by_its_empty_kind()
    {
        Assert.Contains("Odd ()", NoWorkflowWatches.Detail([new WorkflowFolder("Odd", string.Empty, "/odd")], "/x/y.mkv"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_the_manager_gives_no_output_folder_for_changes_only_the_watched_folder()
    {
        var library = new SyncedLibrary("lib", "Movies", "movie", "/new", null, "No output.", SyncedLibraryProblem.NoOutputFolder);
        var workflow = new SyncWorkflow(1, "Movies", "movie", "/old", "/out", 4, "lib", true, [4]);

        var action = Assert.Single(WorkflowSyncRules.Plan(4, [library], [workflow]));

        Assert.Equal((true, false), (action.ChangesWatched, action.ChangesOutput));
    }

    [Fact]
    public void Messages_name_each_folder_the_manager_gave_or_say_there_is_none_yet()
    {
        Assert.Contains("watches /w and writes to no folder yet", WorkflowSyncRules.SetUpMessage("Movies", "Deluno", "/w", null), StringComparison.Ordinal);
        Assert.Contains("watches no folder yet and writes to /o", WorkflowSyncRules.SetUpMessage("Movies", "Deluno", null, "/o"), StringComparison.Ordinal);
        Assert.Contains("watches /w and writes to /o", WorkflowSyncRules.SetUpMessage("Movies", "Deluno", "/w", "/o"), StringComparison.Ordinal);
        Assert.Contains("/w for downloads and /o for cleaned files", WorkflowSyncRules.UpdatedMessage("Movies", "Deluno", "/w", "/o"), StringComparison.Ordinal);
    }
}
