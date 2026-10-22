using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>Every change to the pause is written to Activity, with who made it and until when.</summary>
public sealed class PauseActivityTests
{
    private const string Entries =
        "SELECT group_concat(title || ': ' || detail, ' | ') FROM " +
        "(SELECT title, detail FROM activity_events WHERE event_type LIKE 'system.processing_%' ORDER BY id)";

    [Fact]
    public async Task Pausing_and_resuming_are_in_activity_with_who_did_it()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        using var indefinite = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true, scan_while_paused = true });
        using var resumed = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = false });
        using var timed = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true, pause_for_minutes = 30, scan_while_paused = false });

        Assert.All([indefinite, resumed, timed], response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var entries = (await TestDatabase.ScalarStringAsync(server, Entries))!.Split(" | ");
        Assert.Equal(3, entries.Length);
        Assert.Equal("Processing paused: alice paused processing until you resume it. Weir keeps looking for new files and starts nothing.", entries[0]);
        Assert.Equal("Processing resumed: alice resumed processing.", entries[1]);
        Assert.StartsWith("Processing paused: alice paused processing until 20", entries[2], StringComparison.Ordinal);
        Assert.EndsWith(" UTC. Weir does not look for new files either.", entries[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_the_same_pause_again_or_resuming_when_running_adds_nothing()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        using var running = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = false });
        using var first = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true });
        using var again = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true });

        Assert.All([running, first, again], response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Single((await TestDatabase.ScalarStringAsync(server, Entries))!.Split(" | "));
    }

    [Fact]
    public async Task Changing_only_whether_weir_keeps_looking_leaves_a_timed_pause_ending_when_it_did()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var timed = await Json(await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true, pause_for_minutes = 120 }));

        var unticked = await Json(await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true, scan_while_paused = false }));

        Assert.Equal(timed["paused_until"]!.GetValue<string>(), unticked["paused_until"]!.GetValue<string>());
        Assert.False(unticked["scan_while_paused"]!.GetValue<bool>());
        var indefinite = await Json(await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused = true, pause_for_minutes = (int?)null }));
        Assert.Null(indefinite["paused_until"]);
    }

    [Fact]
    public async Task A_pause_without_the_sessions_confirmation_token_is_refused_in_plain_words_and_changes_nothing()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();

        using var refused = await client.PutAsync("/api/v1/pause", new { csrf_token = "not-the-session-token", paused = true });

        Assert.Equal((HttpStatusCode.BadRequest, "Your confirmation token expired. Refresh the page and try again."), (refused.StatusCode, await Detail(refused)));
        Assert.Null(await TestDatabase.ScalarStringAsync(server, Entries));
    }
}
