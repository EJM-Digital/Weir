using System.Net;
using Weir.Api.Tests.Platform;
using Weir.Core.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// The "Download again" listing (#509) over real HTTP for libraries linked to a Sonarr or Radarr: which titles are offered, the
/// confirmation wording the screen must show first, and the reason a title is not offered. The listing only reads what the last
/// scan stored, so the manager is never called.
/// </summary>
public sealed class LibraryRedownloadListingApiTests
{
    [Fact]
    public async Task A_film_matched_at_radarr_is_offered_with_its_title_the_removed_track_and_the_confirmation_wording()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.FilmPath, "eng");

        using var response = await rig.Admin.GetAsync(RedownloadRig.RedownloadsPath(libraryId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal((libraryId, 1), (body["library_id"]!.GetValue<long>(), body["total"]!.GetValue<int>()));
        var title = body["titles"]!.AsArray().Single()!;
        Assert.Equal(
            (RedownloadRig.FilmPath, "radarr", "Arrival", true, ManagerRedownloadRules.DestructiveConfirmation("Arrival", null), (string?)null),
            (
                title["path"]!.GetValue<string>(),
                title["manager_kind"]!.GetValue<string>(),
                title["manager_title"]!.GetValue<string>(),
                title["can_redownload"]!.GetValue<bool>(),
                title["confirmation_message"]!.GetValue<string>(),
                title["unavailable_reason"]?.GetValue<string>()));
        var track = title["removed_tracks"]!.AsArray().Single()!;
        Assert.Equal(
            ("eng", "audio", "eac3", "Not a language your rules keep"),
            (track["language"]!.GetValue<string>(), track["type"]!.GetValue<string>(), track["codec"]!.GetValue<string>(), track["reason"]!.GetValue<string>()));
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task An_episode_matched_at_sonarr_is_offered_the_same_way()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddSonarrEpisodeAsync();
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.EpisodePath, "eng");

        using var response = await rig.Admin.GetAsync(RedownloadRig.RedownloadsPath(libraryId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var title = (await Json(response))["titles"]!.AsArray().Single()!;
        Assert.Equal(
            (RedownloadRig.EpisodePath, "sonarr", "Severance", true, ManagerRedownloadRules.DestructiveConfirmation("Severance", null)),
            (
                title["path"]!.GetValue<string>(),
                title["manager_kind"]!.GetValue<string>(),
                title["manager_title"]!.GetValue<string>(),
                title["can_redownload"]!.GetValue<bool>(),
                title["confirmation_message"]!.GetValue<string>()));
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task A_film_whose_title_the_scan_did_not_resolve_is_listed_but_not_offered_and_is_told_to_scan_again()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var radarrId = await rig.ConnectAsync("radarr", "http://192.0.2.20:7878");
        var libraryId = await rig.AddLinkedFilmAsync("radarr", radarrId, titleId: null);
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.FilmPath, "eng");

        using var response = await rig.Admin.GetAsync(RedownloadRig.RedownloadsPath(libraryId));

        var title = (await Json(response))["titles"]!.AsArray().Single()!;
        Assert.False(title["can_redownload"]!.GetValue<bool>());
        Assert.Null(title["confirmation_message"]);
        Assert.Equal(
            "Weir does not know which manager file this is, so it cannot ask for a redownload automatically. Scan the library again.",
            title["unavailable_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_film_matched_at_deluno_is_listed_but_not_offered_and_is_told_to_download_it_again_by_hand()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var delunoId = await rig.ConnectAsync("deluno", "http://192.0.2.10:5099");
        var libraryId = await rig.AddLinkedFilmAsync("deluno", delunoId, 7);
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.FilmPath, "eng");

        using var response = await rig.Admin.GetAsync(RedownloadRig.RedownloadsPath(libraryId));

        var title = (await Json(response))["titles"]!.AsArray().Single()!;
        Assert.False(title["can_redownload"]!.GetValue<bool>());
        Assert.Null(title["confirmation_message"]);
        Assert.Equal(ManagerRedownloadRules.NoManagerMessage, title["unavailable_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_library_with_no_manager_link_says_why_nothing_can_be_asked()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var libraryId = await rig.AddUnlinkedFilmAsync();
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.FilmPath, "eng");

        using var response = await rig.Admin.GetAsync(RedownloadRig.RedownloadsPath(libraryId));

        var title = (await Json(response))["titles"]!.AsArray().Single()!;
        Assert.False(title["can_redownload"]!.GetValue<bool>());
        Assert.Equal(ManagerRedownloadRules.NoManagerMessage, title["unavailable_reason"]!.GetValue<string>());
        Assert.Null(title["manager_kind"]);
    }

    [Fact]
    public async Task A_viewer_can_read_the_listing()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.FilmPath, "eng");
        var viewer = await rig.ViewerAsync();

        using var response = await viewer.GetAsync(RedownloadRig.RedownloadsPath(libraryId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await Json(response))["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task Nobody_signed_in_is_refused_the_listing()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        await rig.RecordRemovedTrackAsync(libraryId, RedownloadRig.FilmPath, "eng");
        var anonymous = new ApiTestClient(rig.Server);

        using var response = await anonymous.GetAsync(RedownloadRig.RedownloadsPath(libraryId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_library_that_does_not_exist_is_not_found()
    {
        await using var rig = await RedownloadRig.StartAsync();

        using var response = await rig.Admin.GetAsync(RedownloadRig.RedownloadsPath(9999));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
