using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// The last edges of the media manager rules, each a recorded reply (fixtures/recorded) or a manager's webhook with one member
/// taken out or changed: a path with no root, an answer that is not the shape expected, a setting that is missing.
/// </summary>
public sealed class MediaManagerRemainingEdgeTests
{
    private static WireValue Json(string json) => WireJsonParser.Parse(json);

    private static WireObject Obj(string json) => (WireObject)WireJsonParser.Parse(json);

    [Theory]
    [InlineData(@"\\nas\share", true)]
    [InlineData(@"C:\media", true)]
    [InlineData(@"media\movies", false)]
    [InlineData("/media", true)]
    [InlineData("media/movies", false)]
    [InlineData("media", false)]
    public void A_path_is_rooted_by_its_own_style(string path, bool rooted)
    {
        Assert.Equal(rooted, new ArrOsPath(path).IsRooted);
    }

    [Fact]
    public void A_default_path_has_no_segments_and_no_kind()
    {
        Assert.Empty(default(ArrOsPath).Segments);
        Assert.Equal(ArrOsPath.PathKind.Unknown, default(ArrOsPath).Kind);
    }

    [Theory]
    [InlineData("""{"ok": true}""", false, false)]
    [InlineData("""{"ok": true, "outcome": ""}""", false, false)]
    [InlineData("""{"ok": true, "outcome": "source_gone"}""", true, false)]
    [InlineData("""{"outcome": "live_output_written"}""", false, false)]
    public void A_report_with_no_outcome_is_neither_gone_nor_a_success(string json, bool gone, bool succeeded)
    {
        Assert.Equal(gone, CompletionReports.IsSourceGone(Obj(json)));
        Assert.Equal(succeeded, CompletionReports.IsSucceeded(Obj(json)));
    }

    [Theory]
    [InlineData(@"\", "a", @"\a")]
    [InlineData(@"\\nas", "a", @"\\nas\a")]
    [InlineData(@"X:", "a", @"X:a")]
    [InlineData(@"ab\c", "d", @"ab\c\d")]
    public void A_manager_folder_is_joined_in_its_own_style_wherever_it_is_rooted(string folder, string part, string expected)
    {
        Assert.Equal(expected, CompletionReports.ManagerPathJoin(folder, [part]));
    }

    [Fact]
    public void A_connection_that_is_not_saved_has_no_reference()
    {
        Assert.Null(ConnectionRef.ForManager(null));
        Assert.Null(ConnectionRef.ForDownloadClient(null));
        Assert.Equal(new ConnectionRef(ConnectionKind.DownloadClient, 4), ConnectionRef.ForDownloadClient(4));
    }

    [Theory]
    [InlineData("""{"result": {"move_completed": true, "move_completed_path": "/done", "download_location": "/dl"}, "error": null}""", "/done")]
    [InlineData("""{"result": {"move_completed": false, "move_completed_path": "/done", "download_location": "/dl"}, "error": null}""", "/dl")]
    [InlineData("""{"result": {"download_location": "/dl"}, "error": null}""", "/dl")]
    [InlineData("""{"result": {"move_completed": "yes", "download_location": "/dl"}, "error": null}""", "/dl")]
    [InlineData("""{"result": null, "error": {"code": 1}}""", null)]
    public void The_completed_folder_follows_whether_deluge_moves_completed_downloads(string core, string? expected)
    {
        Assert.Equal(expected, DelugeRules.Parse(Json(core), []).CompletedFolder);
    }

    [Theory]
    [InlineData("""{"result": {"apply_move_completed": true, "move_completed": true, "move_completed_path": "/done/tv"}, "error": null}""", true)]
    [InlineData("""{"result": {"apply_move_completed": false, "move_completed": true, "move_completed_path": "/done/tv"}, "error": null}""", false)]
    [InlineData("""{"result": {"move_completed": true, "move_completed_path": "/done/tv"}, "error": null}""", false)]
    [InlineData("""{"result": {"apply_move_completed": true, "move_completed": false, "move_completed_path": "/done/tv"}, "error": null}""", false)]
    [InlineData("""{"result": null, "error": {"code": 1}}""", false)]
    public void A_deluge_label_has_a_folder_only_when_it_applies_a_move_completed_folder(string options, bool hasFolder)
    {
        var folders = DelugeRules.Parse(null, [("tv", Json(options))]);

        Assert.Equal(hasFolder, folders.CategoryFolders.Count == 1);
    }

    [Theory]
    [InlineData(null, "this workflow")]
    [InlineData("", "this workflow")]
    [InlineData("Films", "the \"Films\" workflow")]
    public void A_library_that_deluno_no_longer_has_is_named_by_the_workflow_when_it_is_known(string? name, string expected)
    {
        Assert.Contains(expected, DelunoDestinationNotices.LibraryRemoved("Deluno", "Movies", name), StringComparison.Ordinal);
    }

    [Fact]
    public void A_destination_with_no_status_or_message_is_unknown_and_says_nothing()
    {
        var libraries = DelunoDestinationRules.ParseLibraries(Json("""
            {"libraries": [{"libraryId": "lib", "libraryName": "Movies", "destinations": [{"downloadClientName": "qBittorrent"}]}]}
            """));

        var destination = Assert.Single(Assert.Single(libraries).Destinations);
        Assert.Equal(DelunoDestination.Unknown, destination.Status);
        Assert.Equal(string.Empty, destination.Message);
        Assert.Equal("category", destination.CategoryKind);
    }

    [Theory]
    [InlineData(@"D:\a", @"E:\b")]
    [InlineData(@"C:\Nas\Movies", "/mnt/nas/movies")]
    [InlineData("/x/Movies", "/y/MOVIES")]
    [InlineData("/Movies", "/y/Movies")]
    [InlineData("/y/Movies", "/Movies")]
    [InlineData("/Movies", "/movies")]
    public void Path_mapping_advice_names_the_differing_prefixes_or_the_whole_paths(string deluno, string weir)
    {
        var advice = DelunoPathMappingAdvice.WhenNoMappingCovers("Deluno", [], deluno, weir, "the watched folder");

        Assert.Contains("Add a path mapping from", advice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x", "Download client")]
    [InlineData(null, "Download client")]
    [InlineData(" QBITTORRENT ", "qBittorrent")]
    public void A_download_clients_product_is_named_or_called_a_download_client(string? kind, string expected)
    {
        Assert.Equal(expected, DownloadClientKinds.ProductLabel(kind));
    }

    [Fact]
    public void A_scan_is_mapped_by_a_mapping_only_when_its_local_path_is_rooted()
    {
        RemotePathMappingEntry[] mappings = [new("host", "/remote", "relative"), new("host", "/remote", "/local")];

        Assert.Equal("/remote/x", DownloadedScanRules.TranslateOutputPath("/local/x", mappings));
    }

    [Theory]
    [InlineData("""{"id": 5, "body": {"path": "/other"}}""", true, 5L)]
    [InlineData("""{"body": {"path": "/other"}}""", true, null)]
    [InlineData("""{"id": 5, "body": {"path": "/mine"}}""", false, null)]
    [InlineData("""{"id": 5, "body": {}}""", false, null)]
    public void Another_scan_is_recognised_by_its_path_and_named_by_its_id_when_it_has_one(string reply, bool another, long? id)
    {
        Assert.Equal(another, DownloadedScanRules.IsAnotherScan(Json(reply), "/mine", out var otherId));
        Assert.Equal(id, otherId);
    }

    [Theory]
    [InlineData("""{"outcome": "skipped_guardrail"}""", HandoffLedgerRules.Skipped)]
    [InlineData("""{"ok": true, "outcome": "live_output_written"}""", HandoffLedgerRules.Completed)]
    [InlineData("""{"ok": true, "outcome": "live_output_written", "passed_through_after_failure": true}""", HandoffLedgerRules.PassedThrough)]
    [InlineData("""{"ok": true, "outcome": "live_output_written", "passed_through_after_failure": false}""", HandoffLedgerRules.Completed)]
    [InlineData("""{"ok": false, "outcome": 4}""", HandoffLedgerRules.Failed)]
    [InlineData("""{"ok": false}""", HandoffLedgerRules.Failed)]
    public void A_hand_offs_result_is_read_from_the_report_the_worker_wrote(string report, string expected)
    {
        Assert.Equal(expected, FolderHandoffReports.TargetResult(Obj(report)));
    }

    [Fact]
    public void A_copy_with_an_outcome_but_no_recorded_speaker_hears_an_import_and_a_refusal_alike()
    {
        var speaker = new ManagerSpeaker("deluno", 1, true);

        Assert.True(HandbackRules.Hears(HandbackRules.NotImported, true, null, speaker, HandbackRules.Imported, false));
    }

    [Fact]
    public void A_manager_path_with_nothing_to_match_does_not_end_with_anything()
    {
        Assert.False(HandbackRules.ManagerPathEndsWith("/a/b", []));
        Assert.Equal(string.Empty, HandbackRules.FileName(string.Empty));
        Assert.Equal(string.Empty, HandbackRules.FileName("/./"));
    }

    [Fact]
    public void A_hand_off_that_names_no_file_is_refused()
    {
        var result = HandoffPaths.RelativeMediaPathForHandoff("/watched", null);

        Assert.Null(result.RelativeMediaPath);
        Assert.Equal("The hand-off did not name a file path.", result.Problem);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(@"C:\Films\", "c:/films")]
    public void Paths_are_compared_without_case_separators_or_a_trailing_slash(string? path, string expected)
    {
        Assert.Equal(expected, LibraryFileChangeRules.ComparablePath(path));
    }

    [Theory]
    [InlineData("""{"eventType": "Download", "series": {"title": "S"}, "episodes": [{"id": 1, "title": "E"}], "episodeFile": {"path": "/tv/a.mkv"}}""")]
    public void An_episode_with_no_season_or_number_has_no_artwork_season_or_episode(string body)
    {
        var imported = ImportEvents.DialectForSource("sonarr")!.Normalize(Obj(body));

        Assert.Null(imported?.Artwork?.Season);
        Assert.Null(imported?.Artwork?.Episode);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("h1", false)]
    public void A_job_key_comes_from_the_hand_off_id_when_there_is_one(string? handoffId, bool fresh)
    {
        var importEvent = new MediaManagerImportEvent { SourceKey = "deluno", EventKind = MediaManagerImportEvent.Handoff, MediaScope = "movie", FilePath = "/m/a.mkv", HandoffId = handoffId };
        var key = IntakeRules.BaseDedupeKey(importEvent, () => Guid.Empty);

        Assert.Equal(fresh, key.EndsWith("00000000000000000000000000000000", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sonarr")]
    public void A_manager_kind_that_is_not_known_has_no_capabilities(string? kind)
    {
        Assert.Equal(kind is not null, ManagerKindProfiles.CapabilitiesForKind(kind) is not null);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(-5L, false)]
    [InlineData(500L, true)]
    [InlineData(1536L, true)]
    public void A_redownload_warning_names_the_size_only_when_it_is_known(long? size, bool named)
    {
        var text = ManagerRedownloadRules.DestructiveConfirmation("Film", size);

        Assert.Equal(named, text.Contains("to download again", StringComparison.Ordinal));
    }

    [Fact]
    public void Download_client_rows_without_a_name_implementation_or_host_are_still_listed()
    {
        var clients = ManagerSetupRules.ParseDownloadClients(
            Json("""
                [{"enable": true, "implementationName": "Sabnzbd", "fields": [{"name": "completedDirectory", "value": "/done"}]},
                 {"enable": true, "fields": []}]
                """),
            MediaManagerKinds.Tv);

        Assert.Equal("Sabnzbd", clients[0].Name);
        Assert.Equal(string.Empty, clients[0].Implementation);
        Assert.Equal("/done", clients[0].Directory);
        Assert.Equal("Download client", clients[1].Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""[]""")]
    [InlineData("""{"config": []}""")]
    [InlineData("""{"config": {}}""")]
    public void A_sabnzbd_config_answer_that_has_no_sections_has_no_folders(string? answer)
    {
        var config = answer is null ? null : Json(answer);

        Assert.Null(SabnzbdRules.ParseCompleteDir(config));
        Assert.Empty(SabnzbdRules.ParseCategoryFolders(config, "/complete"));
    }

    [Fact]
    public void Sabnzbd_categories_without_a_name_or_the_default_one_are_not_folders()
    {
        var folders = SabnzbdRules.ParseCategoryFolders(
            Json("""{"config": {"categories": [{"dir": "a"}, {"name": "*", "dir": "b"}, {"name": "tv", "dir": "tv"}, 7]}}"""),
            "/complete");

        Assert.Equal([new DownloadClientCategoryFolder("tv", "/complete/tv")], folders);
    }

    [Theory]
    [InlineData("""{"result": [{"Name": "Category1.DestDir", "Value": "/x"}, {"Name": "DestDir", "Value": "/d"}]}""")]
    [InlineData("""{"result": [{"Name": "Category1.Name", "Value": ""}, {"Name": "DestDir", "Value": "/d"}]}""")]
    public void An_nzbget_category_with_no_name_is_not_a_folder(string answer)
    {
        Assert.Empty(NzbgetRules.ParseConfig(Json(answer)).CategoryFolders);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""[]""")]
    [InlineData("""{"result": 3}""")]
    public void An_nzbget_answer_that_is_not_a_list_of_options_has_no_folders(string? answer)
    {
        Assert.Null(NzbgetRules.ParseConfig(answer is null ? null : Json(answer)).CompletedFolder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""[]""")]
    [InlineData("""{"arguments": 3}""")]
    public void A_transmission_answer_without_arguments_has_no_folder(string? answer)
    {
        Assert.Null(TransmissionRules.Parse(answer is null ? null : Json(answer)).CompletedFolder);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("""[]""", null)]
    [InlineData("""{"save_path": "/dl"}""", "/dl")]
    public void A_qbittorrent_preferences_answer_gives_its_save_path_when_it_is_one(string? preferences, string? expected)
    {
        var folders = QBittorrentRules.Parse(Json("""{"tv": "x", "movies": {"savePath": "/m"}, "empty": {}}"""), preferences is null ? null : Json(preferences));

        Assert.Equal(expected, folders.CompletedFolder);
        Assert.Equal([new DownloadClientCategoryFolder("movies", "/m")], folders.CategoryFolders);
    }
}
