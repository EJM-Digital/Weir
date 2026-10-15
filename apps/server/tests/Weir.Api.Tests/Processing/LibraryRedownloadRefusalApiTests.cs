using System.Net;
using Weir.Api.Tests.Platform;
using Weir.Core.MediaManagers;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// Everything that must stop "Download again" (#509) before the media manager is asked to do anything, over real HTTP: no
/// confirmation, a bad or missing CSRF token, a viewer, nobody signed in, and a file Weir cannot tie to a title at a manager it
/// can ask. Each test shows the manager received no request at all, so nothing was deleted and nothing was searched for.
/// </summary>
public sealed class LibraryRedownloadRefusalApiTests
{
    [Fact]
    public async Task A_request_that_does_not_confirm_the_delete_is_refused_with_the_reason_and_the_manager_is_never_called()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath, confirm: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("This action deletes the current file before asking for a new one; confirm_destructive must be true.", await Detail(response));
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task A_request_with_no_confirmation_at_all_is_refused_and_the_manager_is_never_called()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.Admin.PostAsync(
            RedownloadRig.RedownloadsPath(libraryId),
            new { path = RedownloadRig.FilmPath, csrf_token = await rig.Admin.CsrfAsync() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task A_request_with_no_path_or_an_extra_field_is_refused_and_the_manager_is_never_called()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var noPath = await rig.Admin.PostAsync(
            RedownloadRig.RedownloadsPath(libraryId),
            new { confirm_destructive = true, csrf_token = await rig.Admin.CsrfAsync() });
        using var extra = await rig.Admin.PostAsync(
            RedownloadRig.RedownloadsPath(libraryId),
            new { path = RedownloadRig.FilmPath, confirm_destructive = true, delete_everything = true, csrf_token = await rig.Admin.CsrfAsync() });

        Assert.Equal((HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity), (noPath.StatusCode, extra.StatusCode));
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task A_request_without_a_csrf_token_is_refused_and_the_manager_is_never_called()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.Admin.PostAsync(
            RedownloadRig.RedownloadsPath(libraryId),
            new { path = RedownloadRig.FilmPath, confirm_destructive = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task A_request_with_a_wrong_csrf_token_is_refused_and_the_manager_is_never_called()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.Admin.PostAsync(
            RedownloadRig.RedownloadsPath(libraryId),
            new { path = RedownloadRig.FilmPath, confirm_destructive = true, csrf_token = "not-the-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Your confirmation token expired. Refresh the page and try again.", await Detail(response));
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task A_viewer_is_refused_and_the_manager_is_never_called()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        var viewer = await rig.ViewerAsync();

        using var response = await rig.RedownloadAsync(viewer, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task Nobody_signed_in_is_refused_and_the_manager_is_never_called()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        var anonymous = new ApiTestClient(rig.Server);

        using var response = await rig.RedownloadAsync(anonymous, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task A_library_that_does_not_exist_is_refused_and_the_manager_is_never_called()
    {
        await using var rig = await RedownloadRig.StartAsync();
        await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.RedownloadAsync(rig.Admin, 9999, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(rig.Manager.Calls);
    }

    [Fact]
    public async Task A_file_with_no_manager_link_is_told_no_manager_can_be_asked_and_nothing_is_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var libraryId = await rig.AddUnlinkedFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        await AssertNothingWasAskedAsync(rig, response, ManagerRedownloadRules.NoManagerMessage);
    }

    [Fact]
    public async Task A_file_the_library_never_scanned_is_told_no_manager_can_be_asked_and_nothing_is_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, "/library/films/Never Scanned (2020)/Never Scanned.mkv");

        await AssertNothingWasAskedAsync(rig, response, ManagerRedownloadRules.NoManagerMessage);
    }

    [Fact]
    public async Task A_file_matched_at_deluno_is_told_no_manager_can_be_asked_and_nothing_is_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var delunoId = await rig.ConnectAsync("deluno", "http://192.0.2.10:5099");
        var libraryId = await rig.AddLinkedFilmAsync("deluno", delunoId, 7);

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        await AssertNothingWasAskedAsync(rig, response, ManagerRedownloadRules.NoManagerMessage);
    }

    [Fact]
    public async Task A_file_whose_title_the_scan_did_not_resolve_is_told_no_manager_can_be_asked_and_nothing_is_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var radarrId = await rig.ConnectAsync("radarr", "http://192.0.2.20:7878");
        var libraryId = await rig.AddLinkedFilmAsync("radarr", radarrId, titleId: null);

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        await AssertNothingWasAskedAsync(rig, response, ManagerRedownloadRules.NoManagerMessage);
    }

    [Fact]
    public async Task A_file_matched_to_a_connection_that_was_since_switched_off_is_told_no_manager_can_be_asked_and_nothing_is_sent()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, connectionId) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        using (var switchedOff = await rig.Admin.PutAsync(
            $"/api/v1/media-managers/connections/{connectionId}",
            new { csrf_token = await rig.Admin.CsrfAsync(), enabled = false }))
        {
            Assert.Equal(HttpStatusCode.OK, switchedOff.StatusCode);
        }

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        await AssertNothingWasAskedAsync(rig, response, ManagerRedownloadRules.NoManagerMessage);
    }

    [Fact]
    public async Task A_sonarr_matched_to_a_film_is_not_asked_and_the_answer_is_in_plain_words()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var sonarrId = await rig.ConnectAsync("sonarr", "http://192.0.2.30:8989");
        var libraryId = await rig.AddLinkedFilmAsync("sonarr", sonarrId, 3);
        rig.Manager.SonarrThatDeletesAndSearches();

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        await AssertNothingWasAskedAsync(rig, response, $"{RedownloadRig.SonarrName} cannot be asked to download this title again.");
    }

    private static async Task AssertNothingWasAskedAsync(RedownloadRig rig, HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(("unsupported", message), (body["outcome"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Empty(rig.Manager.Calls);
    }
}
