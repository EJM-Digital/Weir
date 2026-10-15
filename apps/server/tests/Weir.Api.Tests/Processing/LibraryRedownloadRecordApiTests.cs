using System.Net;
using System.Text.Json.Nodes;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// What "Download again" (#509) leaves behind once it has run, over real HTTP: an Activity entry that says who asked, which
/// manager, what was deleted, whether a search was sent and how it ended, and the note on the Library's listing for a title
/// whose file is gone and whose search did not go through. The manager's replies are assumed shapes
/// (<see cref="ArrRedownloadReplies"/>) until the recordings (#1013, #1014) land.
/// </summary>
public sealed class LibraryRedownloadRecordApiTests
{
    private const string EventType = "library.redownload_requested";

    private static async Task<JsonObject> EntryAsync(RedownloadRig rig, long libraryId)
    {
        using var response = await rig.Admin.GetAsync($"/api/v1/activity/recent?module=library&library_id={libraryId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = (await Json(response))["items"]!.AsArray().Where(item => item!["event_type"]!.GetValue<string>() == EventType).Single()!.AsObject();
        return entry;
    }

    private static JsonObject DetailOf(JsonObject entry) => JsonNode.Parse(entry["detail"]!.GetValue<string>())!.AsObject();

    [Fact]
    public async Task A_download_again_that_went_through_is_recorded_with_who_asked_what_was_deleted_and_the_search_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        var entry = await EntryAsync(rig, libraryId);

        Assert.Equal(
            ("library", "manual", "success", libraryId, RedownloadRig.FilmPath,
                $"Download again for Arrival.mkv: {RedownloadRig.RadarrName} deleted the existing file and is searching for a replacement."),
            (entry["module"]!.GetValue<string>(), entry["trigger"]!.GetValue<string>(), entry["result"]!.GetValue<string>(),
                entry["library_id"]!.GetValue<long>(), entry["relative_path"]!.GetValue<string>(), entry["title"]!.GetValue<string>()));
        var detail = DetailOf(entry);
        Assert.Equal(
            ("alice", "requested", "Radarr", RedownloadRig.RadarrName, "deleted", "sent", ArrRedownloadReplies.MovieFileBytes),
            (detail["requested_by"]!.GetValue<string>(), detail["outcome"]!.GetValue<string>(), detail["provider"]!.GetValue<string>(), detail["manager"]!.GetValue<string>(),
                detail["existing_file"]!.GetValue<string>(), detail["search"]!.GetValue<string>(), detail["existing_file_bytes"]!.GetValue<long>()));
        Assert.Equal(RedownloadRig.FilmPath, detail["relative_media_path"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_episode_download_again_names_the_series_search_and_the_person_who_asked()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddSonarrEpisodeAsync();
        rig.Manager.SonarrThatDeletesAndSearches();
        var operatorClient = await rig.OperatorAsync();
        (await rig.RedownloadAsync(operatorClient, libraryId, RedownloadRig.EpisodePath)).Dispose();

        var detail = DetailOf(await EntryAsync(rig, libraryId));

        Assert.Equal(
            ("carol", "requested", "Sonarr", RedownloadRig.SonarrName, "deleted", "sent"),
            (detail["requested_by"]!.GetValue<string>(), detail["outcome"]!.GetValue<string>(), detail["provider"]!.GetValue<string>(), detail["manager"]!.GetValue<string>(),
                detail["existing_file"]!.GetValue<string>(), detail["search"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_search_that_failed_after_the_delete_is_recorded_as_failed_with_the_file_deleted_and_no_search_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithFile)
            .Deleting(RedownloadScripts.MovieFilePath)
            .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.ManagerError, HttpStatusCode.InternalServerError);
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        var entry = await EntryAsync(rig, libraryId);

        Assert.Equal("failed", entry["result"]!.GetValue<string>());
        var detail = DetailOf(entry);
        Assert.Equal(
            ("deleted_but_search_failed", "deleted", "not_sent"),
            (detail["outcome"]!.GetValue<string>(), detail["existing_file"]!.GetValue<string>(), detail["search"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_delete_Weir_could_not_confirm_is_recorded_as_failed_with_the_file_unknown_and_no_search_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithFile)
            .Json(HttpMethod.Delete, RedownloadScripts.MovieFilePath, ArrRedownloadReplies.ManagerError, HttpStatusCode.InternalServerError);
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        var entry = await EntryAsync(rig, libraryId);

        Assert.Equal("failed", entry["result"]!.GetValue<string>());
        var detail = DetailOf(entry);
        Assert.Equal(
            ("delete_not_confirmed", "unknown", "not_sent"),
            (detail["outcome"]!.GetValue<string>(), detail["existing_file"]!.GetValue<string>(), detail["search"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_manager_that_could_not_be_reached_is_recorded_as_failed_with_nothing_deleted_and_no_search_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.Unreachable(HttpMethod.Get, RedownloadScripts.MoviePath);
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        var entry = await EntryAsync(rig, libraryId);

        Assert.Equal("failed", entry["result"]!.GetValue<string>());
        var detail = DetailOf(entry);
        Assert.Equal(
            ("failed", "not_deleted", "not_sent"),
            (detail["outcome"]!.GetValue<string>(), detail["existing_file"]!.GetValue<string>(), detail["search"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_sonarr_file_that_was_not_matched_is_recorded_as_skipped_with_nothing_deleted_and_no_search_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddSonarrEpisodeAsync();
        rig.Manager.Json(HttpMethod.Get, "/api/v3/episodefile", ArrRedownloadReplies.EpisodeFiles("/data/a.mkv", "/data/b.mkv"));
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.EpisodePath)).Dispose();

        var entry = await EntryAsync(rig, libraryId);

        Assert.Equal("skipped", entry["result"]!.GetValue<string>());
        var detail = DetailOf(entry);
        Assert.Equal(
            ("file_not_matched", "not_deleted", "not_sent"),
            (detail["outcome"]!.GetValue<string>(), detail["existing_file"]!.GetValue<string>(), detail["search"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_file_no_manager_can_be_asked_about_is_recorded_as_skipped()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var libraryId = await rig.AddUnlinkedFilmAsync();
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        var entry = await EntryAsync(rig, libraryId);

        Assert.Equal("skipped", entry["result"]!.GetValue<string>());
        Assert.Equal("unsupported", DetailOf(entry)["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_request_that_is_refused_before_the_manager_is_asked_leaves_nothing_in_Activity()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        var viewer = await rig.ViewerAsync();
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath, confirm: false)).Dispose();
        (await rig.RedownloadAsync(viewer, libraryId, RedownloadRig.FilmPath)).Dispose();

        using var response = await rig.Admin.GetAsync($"/api/v1/activity/recent?module=library&library_id={libraryId}");

        Assert.DoesNotContain((await Json(response))["items"]!.AsArray(), item => item!["event_type"]!.GetValue<string>() == EventType);
    }

    [Fact]
    public async Task The_entry_is_found_by_the_file_filter_on_the_Activity_page()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        using var response = await rig.Admin.GetAsync("/api/v1/activity/recent?file=Arrival.mkv");

        Assert.Single((await Json(response))["items"]!.AsArray(), item => item!["event_type"]!.GetValue<string>() == EventType);
    }

    [Fact]
    public async Task A_title_whose_file_is_gone_and_search_failed_is_marked_waiting_in_the_listing_until_a_search_goes_through()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.FilmPath, "eng");
        Assert.Null(await WaitingNoteAsync(rig, libraryId));
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithFile)
            .Deleting(RedownloadScripts.MovieFilePath)
            .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.ManagerError, HttpStatusCode.InternalServerError);
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        Assert.Equal(
            $"{RedownloadRig.RadarrName} deleted the file, but no search for a new copy went through. Use Download again to search once more.",
            await WaitingNoteAsync(rig, libraryId));

        // The retry finds the movie without a file, so it only searches, and the search is accepted.
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithoutFile)
            .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.CommandAccepted, HttpStatusCode.Created);
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        Assert.Null(await WaitingNoteAsync(rig, libraryId));
    }

    [Fact]
    public async Task A_request_that_does_not_end_in_a_deleted_file_with_no_search_leaves_the_title_unmarked()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.FilmPath, "eng");
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithFile)
            .Json(HttpMethod.Delete, RedownloadScripts.MovieFilePath, ArrRedownloadReplies.ManagerError, HttpStatusCode.InternalServerError);
        (await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath)).Dispose();

        Assert.Null(await WaitingNoteAsync(rig, libraryId));
    }

    private static async Task<string?> WaitingNoteAsync(RedownloadRig rig, long libraryId)
    {
        using var response = await rig.Admin.GetAsync(RedownloadRig.RedownloadsPath(libraryId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response))["titles"]!.AsArray().Single()!["waiting_note"]?.GetValue<string>();
    }
}
