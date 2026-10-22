using System.Net;
using System.Text.Json.Nodes;
using Weir.Api.Tests.Platform;
using Weir.Core.Rules;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>Rules profiles through the real routes: saving one, changing one, removing one, and each reason a request is turned away.</summary>
public sealed class ProcessingRuleSetsApiTests
{
    private const string RuleSets = "/api/v1/processing/rule-sets";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static async Task<HttpResponseMessage> SaveAsync(ApiTestClient client, string name, JsonObject? fields = null, long? replacing = null)
    {
        var body = new JsonObject { ["csrf_token"] = await client.CsrfAsync(), ["name"] = name };
        foreach (var (field, value) in fields ?? new JsonObject())
        {
            body[field] = value?.DeepClone();
        }

        return replacing is { } id ? await client.PutAsync($"{RuleSets}/{id}", body) : await client.PostAsync(RuleSets, body);
    }

    private static async Task<long> CreatedAsync(ApiTestClient client, string name, JsonObject? fields = null)
    {
        using var response = await SaveAsync(client, name, fields);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response))["id"]!.GetValue<long>();
    }

    [Fact]
    public async Task A_profile_saved_with_null_text_fields_gets_the_shipped_defaults_for_each()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var nulls = new JsonObject();
        foreach (var field in new[]
        {
            "primary_audio_lang", "secondary_audio_lang", "tertiary_audio_lang", "subtitle_langs_csv", "audio_sorters_json", "subtitle_sorters_json",
            "original_language_additional_csv", "track_name_template",
        })
        {
            nulls[field] = null;
        }

        using var response = await SaveAsync(client, "Nulls", nulls);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await Json(response);
        Assert.Equal((string.Empty, string.Empty, string.Empty), (saved["primary_audio_lang"]!.GetValue<string>(), saved["subtitle_langs_csv"]!.GetValue<string>(), saved["original_language_additional_csv"]!.GetValue<string>()));
        Assert.StartsWith("[{", saved["audio_sorters_json"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(TrackNaming.DefaultTemplate, saved["track_name_template"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_name_given_to_two_profiles_is_refused_when_saving_and_when_renaming()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var first = await CreatedAsync(client, "Anime");
        var second = await CreatedAsync(client, "Films");

        using var duplicate = await SaveAsync(client, "  Anime  ");
        using var renamed = await SaveAsync(client, "Anime", replacing: second);
        using var keepsOwnName = await SaveAsync(client, "Anime", new JsonObject { ["primary_audio_lang"] = "jpn" }, replacing: first);

        Assert.Equal((HttpStatusCode.BadRequest, "A rule set named 'Anime' already exists."), (duplicate.StatusCode, await Detail(duplicate)));
        Assert.Equal((HttpStatusCode.BadRequest, "A rule set named 'Anime' already exists."), (renamed.StatusCode, await Detail(renamed)));
        Assert.Equal(HttpStatusCode.OK, keepsOwnName.StatusCode);
        Assert.Equal("Films", await TestDatabase.ScalarStringAsync(server, "SELECT name FROM rule_sets WHERE id = $id", ("$id", second)));
    }

    [Theory]
    [InlineData("track_name_template", "{nonsense}")]
    [InlineData("audio_sorters_json", "not json at all")]
    [InlineData("subtitle_sorters_json", "not json at all")]
    public async Task Rules_that_cannot_be_applied_are_a_400_in_plain_words_and_nothing_is_saved(string field, string value)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var before = await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM rule_sets");

        using var response = await SaveAsync(client, "Broken", new JsonObject { [field] = value, ["standardize_track_names"] = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await Detail(response)));
        Assert.Equal(before, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM rule_sets"));
    }

    [Fact]
    public async Task Changing_or_removing_a_profile_that_does_not_exist_is_a_404()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var changed = await SaveAsync(client, "Nothing", replacing: 4040);
        using var removed = await client.SendAsync(HttpMethod.Delete, $"{RuleSets}/4040", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal((HttpStatusCode.NotFound, "That rule set does not exist."), (changed.StatusCode, await Detail(changed)));
        Assert.Equal((HttpStatusCode.NotFound, "That rule set does not exist."), (removed.StatusCode, await Detail(removed)));
    }

    [Fact]
    public async Task A_profile_in_use_cannot_be_removed_until_it_is_not_and_then_it_goes()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var profile = await CreatedAsync(client, "In use");
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, rule_set_id) VALUES ('Films', 'movie', '/in', '/out', $rule) RETURNING id",
            ("$rule", profile));

        using var refused = await client.SendAsync(HttpMethod.Delete, $"{RuleSets}/{profile}", new { csrf_token = await client.CsrfAsync() });
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET rule_set_id = NULL WHERE id = $id", ("$id", libraryId));
        using var removed = await client.SendAsync(HttpMethod.Delete, $"{RuleSets}/{profile}", new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.StartsWith("In use is still used by 1 workflow.", await Detail(refused), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM rule_sets WHERE id = $id", ("$id", profile)));
    }

    [Fact]
    public async Task Changing_a_profile_a_library_cleans_by_queues_one_check_of_that_library_and_names_it()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var profile = await CreatedAsync(client, "Cleaning");
        var other = await CreatedAsync(client, "Unrelated");
        var libraryId = await TestDatabase.ScalarAsync(
            server, "INSERT INTO libraries (name, media_type, watched_folder, output_folder) VALUES ('Films', 'movie', '/in', '/out') RETURNING id");
        await TestDatabase.ExecuteAsync(
            server, "INSERT INTO libraries (name, media_type, watched_folder, output_folder, rule_set_id) VALUES ('Shows', 'tv', '/in2', '/out2', $rule)", ("$rule", profile));
        using var linked = await client.PutAsync(
            $"/api/v1/processing/libraries/{libraryId}/library-settings",
            new { library_folders = new[] { "/library/films" }, library_rule_set_id = profile, csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        using var changed = await SaveAsync(client, "Cleaning", new JsonObject { ["primary_audio_lang"] = "fra" }, replacing: profile);
        using var unrelated = await SaveAsync(client, "Unrelated", new JsonObject { ["primary_audio_lang"] = "fra" }, replacing: other);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var jobIds = (await Json(changed))["library_rescan_job_ids"]!.AsArray().Select(id => id!.GetValue<long>()).ToList();
        var jobId = Assert.Single(jobIds);
        Assert.Equal(
            libraryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await TestDatabase.ScalarStringAsync(server, "SELECT json_extract(payload_json, '$.library_id') FROM jobs WHERE id = $id", ("$id", jobId)));
        Assert.Empty((await Json(unrelated))["library_rescan_job_ids"]!.AsArray());
    }

    [Fact]
    public async Task Saving_changing_or_removing_a_profile_is_for_operators_and_changes_nothing_when_refused()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var profile = await CreatedAsync(client, "Keep");

        await OperatorPostRefusals.AssertAsync(server, RuleSets, token => new { csrf_token = token, name = "Another" });

        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM rule_sets WHERE id = $id", ("$id", profile)));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM rule_sets WHERE name = 'Another'"));
    }

    [Fact]
    public async Task A_profile_with_an_override_that_is_not_an_object_or_has_an_unknown_part_is_a_422()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var notAnObject = await SaveAsync(client, "Overrides", new JsonObject { ["track_name_overrides"] = "forced" });
        using var unknownPart = await SaveAsync(client, "Overrides", new JsonObject { ["track_name_overrides"] = new JsonObject { ["loud"] = "yes" } });
        using var kept = await SaveAsync(client, "Overrides", new JsonObject { ["track_name_overrides"] = new JsonObject { ["forced"] = "Forced", ["commentary"] = null } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notAnObject.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownPart.StatusCode);
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        Assert.Equal("Forced", (await Json(kept))["track_name_overrides"]!["forced"]!.GetValue<string>());
    }
}
