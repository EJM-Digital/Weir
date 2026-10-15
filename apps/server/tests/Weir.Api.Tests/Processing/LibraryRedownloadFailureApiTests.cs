using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// "Download again" (#509) when the manager does not do what it was asked, over real HTTP: a search that fails after the file is
/// deleted says so, and a manager that cannot be reached, refuses Weir or fails the delete leaves the file alone and claims
/// nothing. The manager's replies are assumed shapes (<see cref="ArrRedownloadReplies"/>) until the recordings (#1013, #1014) land.
/// </summary>
public sealed class LibraryRedownloadFailureApiTests
{
    private const string DeletedButNoSearch =
        " deleted the existing file, but Weir could not ask it to search for a replacement. This title now has no file until one is " +
        "searched for again: try Download again, or search for it manually in ";

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_search_the_manager_fails_after_the_file_is_deleted_says_the_file_is_gone_and_no_replacement_was_asked_for(HttpStatusCode searchStatus)
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithFile)
            .Deleting(RedownloadScripts.MovieFilePath)
            .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.ManagerError, searchStatus);

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(
            ("deleted_but_search_failed", RedownloadRig.RadarrName + DeletedButNoSearch + RedownloadRig.RadarrName + "."),
            (body["outcome"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Equal(RedownloadScripts.RadarrDeleteThenSearch, rig.Manager.Asked());
    }

    [Fact]
    public async Task A_manager_that_stops_answering_after_the_delete_is_reported_the_same_way()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddSonarrEpisodeAsync();
        rig.Manager
            .Json(HttpMethod.Get, "/api/v3/episodefile", ArrRedownloadReplies.EpisodeFiles(RedownloadRig.EpisodePath, RedownloadRig.OtherEpisodePath))
            .Deleting(RedownloadScripts.EpisodeFilePath)
            .Unreachable(HttpMethod.Post, "/api/v3/command");

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.EpisodePath);

        var body = await Json(response);
        Assert.Equal(
            ("deleted_but_search_failed", RedownloadRig.SonarrName + DeletedButNoSearch + RedownloadRig.SonarrName + "."),
            (body["outcome"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Equal(RedownloadScripts.SonarrDeleteThenSearch, rig.Manager.Asked());
    }

    [Fact]
    public async Task A_manager_that_cannot_be_reached_is_never_asked_to_delete_and_the_answer_says_it_could_not_be_reached()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.Unreachable(HttpMethod.Get, RedownloadScripts.MoviePath);

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(
            ("failed", $"Weir could not reach {RedownloadRig.RadarrName} at http://192.0.2.20:7878. Check the address is right, and that the app is running and reachable from this machine."),
            (body["outcome"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Equal([$"GET {RedownloadScripts.MoviePath}"], rig.Manager.Asked());
    }

    [Fact]
    public async Task A_manager_that_refuses_the_key_when_looked_up_is_never_asked_to_delete_and_the_answer_says_to_check_the_key()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddSonarrEpisodeAsync();
        rig.Manager.Json(HttpMethod.Get, "/api/v3/episodefile", """{"error":"Unauthorized"}""", HttpStatusCode.Unauthorized);

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.EpisodePath);

        var body = await Json(response);
        Assert.Equal(
            ("failed", $"{RedownloadRig.SonarrName} refused Weir's API key, so it could not say whether it can download this again. Check the key in Setup › Connections › Media managers and save it again."),
            (body["outcome"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Equal(["GET /api/v3/episodefile?seriesId=3"], rig.Manager.Asked());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "did not give Weir the answer it expected when asked whether it can download this again. Check the address points at Radarr on 192.0.2.20 itself, not a page inside it, and that Radarr on 192.0.2.20 is running normally.")]
    [InlineData(HttpStatusCode.Unauthorized, "refused Weir's API key, so it could not say whether it can download this again. Check the key in Setup › Connections › Media managers and save it again.")]
    [InlineData(HttpStatusCode.Forbidden, "refused Weir's API key, so it could not say whether it can download this again. Check the key in Setup › Connections › Media managers and save it again.")]
    public async Task A_manager_that_fails_the_delete_is_never_asked_to_search_and_nothing_is_said_to_be_deleted(HttpStatusCode deleteStatus, string reason)
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithFile)
            .Json(HttpMethod.Delete, RedownloadScripts.MovieFilePath, ArrRedownloadReplies.ManagerError, deleteStatus)
            .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.CommandAccepted, HttpStatusCode.Created);

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(("failed", $"{RedownloadRig.RadarrName} {reason}"), (body["outcome"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Equal([$"GET {RedownloadScripts.MoviePath}", $"DELETE {RedownloadScripts.MovieFilePath}"], rig.Manager.Asked());
    }

    [Fact]
    public async Task A_delete_that_cannot_be_sent_is_never_followed_by_a_search()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithFile)
            .Unreachable(HttpMethod.Delete, RedownloadScripts.MovieFilePath)
            .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.CommandAccepted, HttpStatusCode.Created);

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        var body = await Json(response);
        Assert.Equal("failed", body["outcome"]!.GetValue<string>());
        Assert.DoesNotContain("deleted the existing file", body["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([$"GET {RedownloadScripts.MoviePath}", $"DELETE {RedownloadScripts.MovieFilePath}"], rig.Manager.Asked());
    }

    [Fact]
    public async Task A_manager_that_is_rate_limiting_Weir_is_never_asked_to_delete_and_the_answer_says_so()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.Route(HttpMethod.Get, RedownloadScripts.MoviePath, _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        var body = await Json(response);
        Assert.Equal("failed", body["outcome"]!.GetValue<string>());
        Assert.Contains("is rate limiting Weir", body["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([$"GET {RedownloadScripts.MoviePath}"], rig.Manager.Asked());
    }
}
