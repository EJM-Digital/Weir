using System.Net;
using Weir.Api.Tests.Platform;
using Weir.Core.Json;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// "Download again" (#509) when it goes right, over real HTTP against a scripted Radarr and Sonarr: the file is deleted at the
/// manager first, then the manager is asked to search, and the answer says both happened. The replies the managers give are
/// assumed shapes (<see cref="ArrRedownloadReplies"/>) until the Sonarr and Radarr recordings (#1013, #1014) replace them.
/// </summary>
public sealed class LibraryRedownloadRequestApiTests
{
    [Fact]
    public async Task A_film_is_looked_up_then_its_file_deleted_then_a_search_asked_for_in_that_order_and_the_answer_says_so()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(
            ("requested", RedownloadRig.FilmPath, $"{RedownloadRig.RadarrName} deleted the existing file and is searching for a replacement."),
            (body["outcome"]!.GetValue<string>(), body["path"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Equal(RedownloadScripts.RadarrDeleteThenSearch, rig.Manager.Asked());
    }

    [Fact]
    public async Task The_answer_still_comes_back_when_the_activity_entry_for_it_cannot_be_written()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        await TestDatabase.ExecuteAsync(rig.Server, "DROP TABLE activity_events");

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("requested", (await Json(response))["outcome"]!.GetValue<string>());
        Assert.Equal(RedownloadScripts.RadarrDeleteThenSearch, rig.Manager.Asked());
    }

    [Fact]
    public async Task An_operator_who_is_not_an_administrator_can_ask_for_the_download_again()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        var operatorClient = await rig.OperatorAsync();

        using var response = await rig.RedownloadAsync(operatorClient, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RedownloadScripts.RadarrDeleteThenSearch, rig.Manager.Asked());
    }

    [Fact]
    public async Task The_search_asked_of_radarr_names_the_movie_and_every_call_carries_the_connections_key()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($$"""{"name":"MoviesSearch","movieIds":[{{ArrRedownloadReplies.MovieId}}]}""", Compact(rig.Manager.SearchBody()));
        Assert.All(rig.Manager.Requested(), call => Assert.Equal("the-managers-api-key", call.ApiKey));
        Assert.Empty(rig.Manager.Requested().Single(call => call.Method == "DELETE").Body);
    }

    [Fact]
    public async Task An_episode_is_found_by_its_path_so_only_that_file_is_deleted_and_the_series_is_searched()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddSonarrEpisodeAsync();
        rig.Manager.SonarrThatDeletesAndSearches();

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.EpisodePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(
            ("requested", $"{RedownloadRig.SonarrName} deleted the existing file and is searching for a replacement."),
            (body["outcome"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Equal(RedownloadScripts.SonarrDeleteThenSearch, rig.Manager.Asked());
        Assert.Equal($$"""{"name":"SeriesSearch","seriesId":{{ArrRedownloadReplies.SeriesId}}}""", Compact(rig.Manager.SearchBody()));
    }

    [Fact]
    public async Task The_other_episode_of_the_series_is_deleted_when_that_is_the_file_asked_about()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddSonarrEpisodeAsync();
        rig.Manager.SonarrThatDeletesAndSearches().Deleting($"/api/v3/episodefile/{ArrRedownloadReplies.OtherEpisodeFileId}");

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.OtherEpisodePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [
                $"GET /api/v3/episodefile?seriesId={ArrRedownloadReplies.SeriesId}",
                $"DELETE /api/v3/episodefile/{ArrRedownloadReplies.OtherEpisodeFileId}",
                "POST /api/v3/command",
            ],
            rig.Manager.Asked());
    }

    [Fact]
    public async Task A_film_the_manager_already_has_no_file_for_is_only_searched_for_and_nothing_is_deleted()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager
            .Json(HttpMethod.Get, RedownloadScripts.MoviePath, ArrRedownloadReplies.MovieWithoutFile)
            .Json(HttpMethod.Post, "/api/v3/command", ArrRedownloadReplies.CommandAccepted, HttpStatusCode.Created);

        using var response = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(
            ("requested", $"{RedownloadRig.RadarrName} is searching for this title; it had no existing file for Weir to delete first."),
            (body["outcome"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
        Assert.Equal([$"GET {RedownloadScripts.MoviePath}", "POST /api/v3/command"], rig.Manager.Asked());
    }

    private static string Compact(string json) => WireJsonWriter.Dumps(WireJsonParser.Parse(json)!, WireJsonFormat.Compact);
}
