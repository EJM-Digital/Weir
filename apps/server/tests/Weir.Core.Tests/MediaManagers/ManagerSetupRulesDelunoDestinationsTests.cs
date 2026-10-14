using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="ManagerSetupRules.EvaluateDelunoWithDestinations"/>: what Deluno's published download destinations and path
/// mappings let Weir say about where a workflow's downloads land.
/// </summary>
public sealed class ManagerSetupRulesDelunoDestinationsTests
{
    private const string Mappings = "Settings › Media Management › Processing Workflow › Weir › Path mappings";

    private static readonly ManagerLibraryDescriptor RefiningMovies =
        new("lib-movies", "Movies", MediaManagerKinds.Movie, "C:\\Media\\Movies", "C:\\Weir\\Ready\\Movies", true, "C:\\NasMount\\Completed\\Movies");

    private static DelunoDestination Destination(
        string status = DelunoDestination.Ok,
        string? saveFolder = "C:\\NasMount\\Completed\\Movies",
        string message = "",
        string savedBy = "deluno-per-grab") =>
        new("qBittorrent", "radarr", "category", saveFolder, savedBy, status, message);

    private static DelunoLibraryDestinations Library(
        string? downloads = "C:\\NasMount\\Completed\\Movies",
        string? processed = "C:\\NasMount\\Ready\\Movies",
        IReadOnlyList<DelunoDestination>? destinations = null,
        (string Deluno, string Weir)[]? mappings = null) =>
        new(
            "lib-movies",
            "Movies",
            downloads,
            processed,
            destinations ?? [Destination()],
            [.. (mappings ?? []).Select(mapping => new DelunoPathMapping(mapping.Deluno, mapping.Weir))]);

    private static DelunoSetupResult Evaluate(
        DelunoDestinationsAnswer answer,
        string watched = "C:\\Downloads\\Completed\\Movies",
        string output = "C:\\Weir\\Ready\\Movies",
        IFolderProbe? probe = null,
        string? preferredKey = null)
    {
        var choice = ManagerSetupRules.ChooseDelunoLibrary("Deluno", MediaManagerKinds.Movie, [RefiningMovies], preferredKey);
        return ManagerSetupRules.EvaluateDelunoWithDestinations("Deluno", MediaManagerKinds.Movie, watched, output, choice, null, answer, probe);
    }

    private static DelunoSetupResult Evaluate(DelunoLibraryDestinations library, string watched = "C:\\Downloads\\Completed\\Movies", string output = "C:\\Weir\\Ready\\Movies", IFolderProbe? probe = null) =>
        Evaluate(DelunoDestinationsAnswer.Read([library]), watched, output, probe);

    private static SetupCheckLine LineAbout(DelunoSetupResult result, string text) =>
        Assert.Single(result.Lines, line => line.Text.Contains(text, StringComparison.Ordinal));

    private sealed class LinkedFolders(Dictionary<string, string> finalPaths) : IFolderProbe
    {
        public bool Exists(string path) => true;

        public bool CanRead(string path) => true;

        public bool CanWrite(string path) => true;

        public string? ResolveFinalPath(string path) => finalPaths.GetValueOrDefault(path);

        public bool? SameFilesystem(string first, string second) => true;
    }

    [Fact]
    public void A_client_saving_inside_the_watched_folder_is_ok_and_says_which_client_and_category_save_there()
    {
        var result = Evaluate(Library(destinations: [Destination(saveFolder: "C:\\Downloads\\Completed\\Movies\\radarr")]));

        var line = LineAbout(result, "qBittorrent");
        Assert.Equal(SetupCheckLine.Ok, line.State);
        Assert.Equal(
            "Deluno's qBittorrent saves the \"radarr\" category in C:\\Downloads\\Completed\\Movies\\radarr, inside this workflow's watched folder.",
            line.Text);
    }

    [Fact]
    public void A_label_based_client_is_named_by_its_label()
    {
        var destination = new DelunoDestination("Deluge", "weir", "label", "C:\\Downloads\\Completed\\Movies", "client-category", DelunoDestination.Ok, string.Empty);

        var result = Evaluate(Library(destinations: [destination]));

        Assert.Equal(
            "Deluno's Deluge saves the \"weir\" label in C:\\Downloads\\Completed\\Movies, inside this workflow's watched folder.",
            LineAbout(result, "Deluge").Text);
    }

    [Fact]
    public void A_client_category_folder_outside_the_watched_folder_needs_a_fix_naming_client_category_folders_and_the_change()
    {
        var result = Evaluate(Library(destinations: [Destination(saveFolder: "D:\\Elsewhere\\Movies", savedBy: DelunoDestination.SavedByClientCategory)]));

        var line = LineAbout(result, "qBittorrent");
        Assert.Equal(SetupCheckLine.Problem, line.State);
        Assert.Equal(
            "Deluno's qBittorrent saves the \"radarr\" category in D:\\Elsewhere\\Movies, which isn't inside this workflow's watched folder C:\\Downloads\\Completed\\Movies. " +
            "Set where qBittorrent saves the \"radarr\" category to this workflow's watched folder, or set the watched folder to D:\\Elsewhere\\Movies.",
            line.Text);
    }

    [Fact]
    public void A_folder_deluno_picks_per_grab_that_lies_outside_points_at_the_libraries_downloads_folder()
    {
        var result = Evaluate(Library(destinations: [Destination(saveFolder: "D:\\Elsewhere\\Movies")]));

        Assert.Equal(
            "Deluno's qBittorrent saves the \"radarr\" category in D:\\Elsewhere\\Movies, which isn't inside this workflow's watched folder C:\\Downloads\\Completed\\Movies. " +
            "Deluno picks this folder for each download from the library's downloads folder. Set the watched folder to D:\\Elsewhere\\Movies, or change the library's downloads folder in Deluno.",
            LineAbout(result, "qBittorrent").Text);
    }

    [Fact]
    public void A_destination_deluno_calls_a_problem_shows_its_message()
    {
        var result = Evaluate(Library(destinations: [Destination(DelunoDestination.Problem, null, "qBittorrent has no path mapping for the downloads folder.")]));

        var line = LineAbout(result, "qBittorrent");
        Assert.Equal(SetupCheckLine.Problem, line.State);
        Assert.Equal(
            "Deluno reports a problem with qBittorrent for the \"radarr\" category: qBittorrent has no path mapping for the downloads folder.",
            line.Text);
    }

    [Fact]
    public void A_destination_deluno_could_not_get_an_answer_for_is_not_verified_and_says_so()
    {
        var result = Evaluate(Library(destinations: [Destination(DelunoDestination.Unknown, null, "Timed out.")]));

        var line = LineAbout(result, "qBittorrent");
        Assert.Equal(SetupCheckLine.Unverified, line.State);
        Assert.Equal(
            "Deluno couldn't get an answer from qBittorrent about where it saves the \"radarr\" category, so Weir can't verify those downloads land inside the watched folder.",
            line.Text);
    }

    [Fact]
    public void A_destination_reported_ok_without_a_folder_is_not_verified()
    {
        var result = Evaluate(Library(destinations: [Destination(saveFolder: null)]));

        Assert.Equal(SetupCheckLine.Unverified, LineAbout(result, "didn't say where qBittorrent saves").State);
    }

    [Fact]
    public void A_downloads_folder_reached_through_a_mapping_into_the_watched_folder_is_ok_and_shows_both_paths()
    {
        var result = Evaluate(Library(destinations: [], mappings: [("C:\\NasMount", "C:\\Downloads")]));

        var line = LineAbout(result, "finishes downloads");
        Assert.Equal(SetupCheckLine.Ok, line.State);
        Assert.Equal(
            "Deluno's Movies library finishes downloads in C:\\NasMount\\Completed\\Movies (Weir sees it as C:\\Downloads\\Completed\\Movies), inside this workflow's watched folder.",
            line.Text);
        Assert.Equal("C:\\Downloads\\Completed\\Movies", result.WatchedFolder);
    }

    [Fact]
    public void A_save_folder_is_mapped_before_it_is_compared_with_the_watched_folder()
    {
        var result = Evaluate(Library(destinations: [Destination(saveFolder: "C:\\NasMount\\Completed\\Movies\\radarr")], mappings: [("C:\\NasMount", "C:\\Downloads")]));

        Assert.Equal(
            "Deluno's qBittorrent saves the \"radarr\" category in C:\\NasMount\\Completed\\Movies\\radarr (Weir sees it as C:\\Downloads\\Completed\\Movies\\radarr), inside this workflow's watched folder.",
            LineAbout(result, "qBittorrent").Text);
    }

    [Fact]
    public void A_downloads_folder_no_mapping_covers_needs_a_fix_that_says_deluno_has_no_mappings()
    {
        var result = Evaluate(Library(destinations: []));

        var line = LineAbout(result, "finishes downloads");
        Assert.Equal(SetupCheckLine.Problem, line.State);
        Assert.Equal(
            "Deluno's Movies library finishes downloads in C:\\NasMount\\Completed\\Movies, which isn't inside this workflow's watched folder C:\\Downloads\\Completed\\Movies. " +
            $"Deluno has no path mappings for Weir. Add a path mapping from C:\\NasMount to C:\\Downloads in Deluno ({Mappings}), or set the watched folder to C:\\NasMount\\Completed\\Movies.",
            line.Text);
    }

    [Fact]
    public void A_downloads_folder_none_of_the_mappings_covers_names_the_mappings_deluno_does_have()
    {
        var result = Evaluate(Library(destinations: [], mappings: [("D:\\Other", "E:\\Weir"), ("F:\\More", "G:\\Weir")]));

        Assert.Equal(
            "Deluno's Movies library finishes downloads in C:\\NasMount\\Completed\\Movies, which isn't inside this workflow's watched folder C:\\Downloads\\Completed\\Movies. " +
            "None of Deluno's path mappings for Weir covers it (D:\\Other to E:\\Weir; F:\\More to G:\\Weir). " +
            $"Add a path mapping from C:\\NasMount to C:\\Downloads in Deluno ({Mappings}), or set the watched folder to C:\\NasMount\\Completed\\Movies.",
            LineAbout(result, "finishes downloads").Text);
    }

    [Fact]
    public void A_downloads_folder_a_mapping_carries_to_the_wrong_place_says_to_use_that_folder_or_fix_the_mapping()
    {
        var result = Evaluate(Library(destinations: [], mappings: [("C:\\NasMount", "Z:\\Nas")]));

        Assert.Equal(
            "Deluno's Movies library finishes downloads in C:\\NasMount\\Completed\\Movies (Weir sees it as Z:\\Nas\\Completed\\Movies), which isn't inside this workflow's watched folder C:\\Downloads\\Completed\\Movies. " +
            $"Set the watched folder to Z:\\Nas\\Completed\\Movies, or correct the Weir folder of that path mapping in Deluno ({Mappings}).",
            LineAbout(result, "finishes downloads").Text);
    }

    [Fact]
    public void A_downloads_folder_that_leads_into_the_watched_folder_through_a_link_is_ok_after_mapping()
    {
        var links = new LinkedFolders(new()
        {
            ["Z:\\Nas\\Completed\\Movies"] = "C:\\Downloads\\Completed\\Movies",
            ["C:\\Downloads\\Completed\\Movies"] = "C:\\Downloads\\Completed\\Movies",
        });

        var result = Evaluate(Library(destinations: [], mappings: [("C:\\NasMount", "Z:\\Nas")]), probe: links);

        Assert.Equal(SetupCheckLine.Ok, LineAbout(result, "finishes downloads").State);
    }

    [Fact]
    public void A_downloads_folder_with_no_watched_folder_to_compare_is_a_problem_naming_the_mapped_folder_to_set()
    {
        var result = Evaluate(Library(destinations: [], mappings: [("C:\\NasMount", "C:\\Downloads")]), watched: string.Empty);

        var line = LineAbout(result, "has no watched folder");
        Assert.Equal(SetupCheckLine.Problem, line.State);
        Assert.Equal(
            "Deluno reports Movies's downloads in C:\\NasMount\\Completed\\Movies (Weir sees it as C:\\Downloads\\Completed\\Movies), but this workflow has no watched folder. " +
            "Set the watched folder to C:\\Downloads\\Completed\\Movies.",
            line.Text);
    }

    [Fact]
    public void A_library_with_no_downloads_folder_says_so_and_stays_not_verified()
    {
        var result = Evaluate(Library(downloads: null, destinations: []));

        Assert.Equal(SetupCheckLine.Unverified, LineAbout(result, "does not say where Movies's downloads finish").State);
        Assert.Null(result.WatchedFolder);
    }

    [Fact]
    public void A_library_whose_downloads_folder_is_empty_is_judged_by_where_its_clients_save()
    {
        var result = Evaluate(Library(downloads: null, destinations: [Destination(saveFolder: "C:\\Downloads\\Completed\\Movies")]));

        Assert.DoesNotContain(result.Lines, line => line.Text.Contains("does not say where", StringComparison.Ordinal));
        Assert.Equal(SetupCheckLine.Ok, LineAbout(result, "qBittorrent").State);
        Assert.Equal("C:\\Downloads\\Completed\\Movies", result.WatchedFolder);
    }

    [Fact]
    public void An_output_folder_that_is_the_workflows_output_folder_after_mapping_is_ok()
    {
        var result = Evaluate(Library(destinations: [], mappings: [("C:\\NasMount", "C:\\Weir")]));

        var line = LineAbout(result, "picks up cleaned files");
        Assert.Equal(SetupCheckLine.Ok, line.State);
        Assert.Equal(
            "Deluno picks up cleaned files from C:\\NasMount\\Ready\\Movies (Weir sees it as C:\\Weir\\Ready\\Movies), the folder this workflow writes to.",
            line.Text);
        Assert.Equal("C:\\Weir\\Ready\\Movies", result.OutputFolder);
    }

    [Fact]
    public void An_output_folder_that_is_not_the_workflows_output_folder_needs_a_fix()
    {
        var result = Evaluate(Library(destinations: [], processed: "C:\\NasMount\\Ready\\Movies", mappings: [("C:\\NasMount", "C:\\Weir")]), output: "C:\\Elsewhere");

        var line = LineAbout(result, "picks up cleaned files");
        Assert.Equal(SetupCheckLine.Problem, line.State);
        Assert.Equal(
            "Deluno picks up cleaned files from C:\\NasMount\\Ready\\Movies (Weir sees it as C:\\Weir\\Ready\\Movies), but this workflow writes to C:\\Elsewhere. " +
            $"Set the output folder to C:\\Weir\\Ready\\Movies, or correct the Weir folder of that path mapping in Deluno ({Mappings}).",
            line.Text);
    }

    [Fact]
    public void A_library_that_uses_no_processor_has_no_output_line()
    {
        var result = Evaluate(Library(destinations: [], processed: null));

        Assert.DoesNotContain(result.Lines, line => line.Text.Contains("picks up cleaned files", StringComparison.Ordinal));
        Assert.Null(result.OutputFolder);
    }

    [Fact]
    public void An_older_deluno_keeps_the_manifest_lines_and_says_which_release_lets_weir_check()
    {
        var result = Evaluate(DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.NotOffered), watched: "C:\\Downloads\\Completed\\Movies");

        var expected = ManagerSetupRules.EvaluateDeluno("Deluno", MediaManagerKinds.Movie, "C:\\Downloads\\Completed\\Movies", "C:\\Weir\\Ready\\Movies", [RefiningMovies]);
        Assert.Equal(expected.Lines, result.Lines.Take(expected.Lines.Count));
        var note = Assert.Single(result.Lines.Skip(expected.Lines.Count));
        Assert.Equal(SetupCheckLine.Note, note.State);
        Assert.Equal("Deluno 1.0.0-rc.23 or later lets Weir check where its download clients save and how its path mappings apply.", note.Text);
    }

    [Fact]
    public void A_library_deluno_says_it_no_longer_has_is_named_with_what_to_do_and_is_not_called_too_old()
    {
        var result = Evaluate(DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.LibraryNotFound));

        var line = Assert.Single(result.Lines);
        Assert.Equal(SetupCheckLine.Unverified, line.State);
        Assert.Equal(
            "Deluno no longer has the Movies library this workflow came from, so Weir can't check where its downloads go. " +
            "Remove this workflow if the library is gone for good, or unlink it from Deluno to keep it as a Weir-only workflow.",
            line.Text);
        Assert.Null(result.WatchedFolder);
        Assert.Null(result.OutputFolder);
    }

    [Fact]
    public void A_library_deluno_no_longer_has_names_the_workflow_when_the_check_is_for_one()
    {
        var choice = ManagerSetupRules.ChooseDelunoLibrary("Deluno", MediaManagerKinds.Movie, [RefiningMovies]);

        var result = ManagerSetupRules.EvaluateDelunoWithDestinations(
            "Deluno",
            MediaManagerKinds.Movie,
            "C:\\Downloads",
            "C:\\Ready",
            choice,
            null,
            DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.LibraryNotFound),
            workflowName: "Family films");

        Assert.Equal(
            "Deluno no longer has the Movies library that the \"Family films\" workflow came from, so Weir can't check where its downloads go. " +
            "Remove the \"Family films\" workflow if the library is gone for good, or unlink it from Deluno to keep it as a Weir-only workflow.",
            Assert.Single(result.Lines).Text);
    }

    [Fact]
    public void A_key_without_the_imports_scope_gets_a_plain_fix_ahead_of_the_manifest_lines()
    {
        var result = Evaluate(DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.NeedsImportsScope));

        var fix = result.Lines[0];
        Assert.Equal(SetupCheckLine.Unverified, fix.State);
        Assert.Equal(
            "The API key Weir uses for Deluno can't read where downloads go. Give it the Imports permission: in Deluno, open System › API Access and create a key with Media automation access (it includes Imports), then save that key under Setup › Connections › Media managers in Weir.",
            fix.Text);
        Assert.True(result.Lines.Count > 1);
    }

    [Fact]
    public void A_deluno_that_could_not_be_asked_says_so_after_the_manifest_lines()
    {
        var result = Evaluate(DelunoDestinationsAnswer.Failed(DelunoDestinationsStatus.Unreachable, "Weir could not reach Deluno at http://192.0.2.10:5099."));

        var last = result.Lines[^1];
        Assert.Equal(SetupCheckLine.Unverified, last.State);
        Assert.Equal("Weir could not reach Deluno at http://192.0.2.10:5099.", last.Text);
        Assert.DoesNotContain(result.Lines, line => line.State == SetupCheckLine.Problem);
    }

    [Fact]
    public void A_linked_library_deluno_no_longer_has_says_so_and_is_not_verified()
    {
        var choice = ManagerSetupRules.ChooseDelunoLibrary("Deluno", MediaManagerKinds.Movie, [RefiningMovies], preferredKey: "lib-gone");

        Assert.Null(choice.Library);
        var line = Assert.Single(choice.Lines);
        Assert.Equal(SetupCheckLine.Unverified, line.State);
        Assert.Equal(
            "Deluno no longer has the library this workflow came from, so Weir can't check where its downloads go. " +
            "Remove this workflow if the library is gone for good, or unlink it from Deluno to keep it as a Weir-only workflow.",
            line.Text);
    }

    [Fact]
    public void An_answer_that_does_not_list_the_chosen_library_says_deluno_no_longer_has_it()
    {
        var elsewhere = new DelunoLibraryDestinations("lib-other", "Other", null, null, [], []);

        var result = Evaluate(DelunoDestinationsAnswer.Read([elsewhere]));

        Assert.Equal(SetupCheckLine.Unverified, Assert.Single(result.Lines).State);
        Assert.Contains("no longer has the library", Assert.Single(result.Lines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_library_a_workflow_was_created_from_is_the_one_checked_when_deluno_has_several()
    {
        var second = RefiningMovies with { Key = "lib-second", Name = "Movies 4K" };

        var choice = ManagerSetupRules.ChooseDelunoLibrary("Deluno", MediaManagerKinds.Movie, [RefiningMovies, second], preferredKey: "lib-second");

        Assert.Equal("lib-second", choice.Library!.Key);
        Assert.Empty(choice.Lines);
    }

    [Fact]
    public void With_no_preferred_library_the_first_refining_one_is_checked_and_the_others_are_mentioned()
    {
        var second = RefiningMovies with { Key = "lib-second", Name = "Movies 4K" };

        var choice = ManagerSetupRules.ChooseDelunoLibrary("Deluno", MediaManagerKinds.Movie, [RefiningMovies, second]);

        Assert.Equal("lib-movies", choice.Library!.Key);
        Assert.Equal(SetupCheckLine.Note, Assert.Single(choice.Lines).State);
    }

    [Fact]
    public void A_library_that_is_no_longer_set_to_refine_before_import_is_a_problem_naming_it()
    {
        var plain = RefiningMovies with { ProcessesBeforeImport = false };

        var choice = ManagerSetupRules.ChooseDelunoLibrary("Deluno", MediaManagerKinds.Movie, [plain, RefiningMovies with { Key = "lib-other" }], preferredKey: "lib-movies");

        Assert.Null(choice.Library);
        Assert.Equal(
            "Deluno's Movies library is no longer set to Refine before import, so Deluno will not hand movie downloads to Weir. Choose Refine before import for that library in Deluno.",
            Assert.Single(choice.Lines).Text);
    }
}
