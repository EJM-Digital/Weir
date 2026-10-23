using System.Net;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>The hand-off outcome route's body, and the answers it gives for a hand-off from a source with no product name.</summary>
public sealed partial class HandbackOutcomeApiTests
{
    [Theory]
    [InlineData("""{"outcome":"imported","occurredUtc":null}""", "datetime_type")]
    [InlineData("""{"outcome":"imported","occurredUtc":"yesterday"}""", "datetime_parsing")]
    public async Task A_time_that_is_empty_or_unreadable_is_refused_before_the_hand_off_is_looked_up(string body, string issue)
    {
        await using var server = await StartAsync();

        using var response = await PostOutcomeAsync(server, "h1", TestDatabase.RawJson(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var first = (await Json(response))["detail"]![0]!;
        Assert.Equal((issue, "occurredUtc"), (first["type"]!.GetValue<string>(), first["loc"]![1]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_outcome_for_a_hand_off_from_a_source_with_no_product_name_still_gets_the_plain_answer()
    {
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO media_manager_handoffs (source_key, handoff_id, library_id, relative_path, state) VALUES ('native', 'n1', $l, 'Film/film.mkv', 'queued')",
            ("$l", library));

        using var response = await new ApiTestClient(server).SendAsync(
            HttpMethod.Post, "/api/v1/intake/handoffs/native/n1/outcome", headers: SecretHeader, content: DelunoOutcome("imported", null, null));

        Assert.Equal(
            (HttpStatusCode.Conflict, "Weir has not finished this hand-off yet (it is queued), so there is no file to import."),
            (response.StatusCode, await Detail(response)));
    }

    [Fact]
    public async Task An_outcome_recorded_without_a_message_is_answered_again_with_an_empty_one()
    {
        await using var server = await StartAsync();
        await FinishedHandoffAsync(server);
        using (var first = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE media_manager_handoffs SET outcome_message = NULL");
        using var again = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(string.Empty, (await Json(again))["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refusal_after_an_import_from_a_source_with_no_product_name_says_the_media_manager_already_said_so()
    {
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO media_manager_handoffs (source_key, handoff_id, library_id, relative_path, state, outcome) VALUES ('native', 'n2', $l, 'Film/film.mkv', 'queued', 'imported')",
            ("$l", library));

        using var response = await new ApiTestClient(server).SendAsync(
            HttpMethod.Post, "/api/v1/intake/handoffs/native/n2/outcome", headers: SecretHeader, content: DelunoOutcome("not-imported", null, "Not for me."));

        Assert.Equal(
            (HttpStatusCode.Conflict, "Your media manager already said it imported this file, so Weir kept that answer."),
            (response.StatusCode, await Detail(response)));
    }
}
