using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>The intake webhook: which Sonarr, Radarr, Deluno and native events queue work, and its secret.</summary>
[ContractArea("media_managers")]
public sealed class IntakeTests(IntakeFixture fixture) : IClassFixture<IntakeFixture>
{
    private const string Webhook = $"{WeirClient.Api}/intake/webhook";

    private WeirServer Server => fixture.Server;

    // --- the dialects that replaced the per-vendor webhook routes -----------------

    [Fact]
    public async Task Sonarr_non_download_is_ignored()
    {
        using var anonymous = Server.CreateClient();

        var response = await anonymous.PostAsync(
            $"{Webhook}/sonarr", new JsonObject { ["eventType"] = "Grab", ["episodes"] = new JsonArray(new JsonObject { ["id"] = 1 }) });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["status"] = "ignored", ["source"] = "sonarr" }, response.Fields), response.ToString());
    }

    [Fact]
    public async Task Sonarr_download_is_accepted_and_ignored()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var before = (await RemuxJobs.ListAsync(admin)).Count;

        var response = await admin.PostAsync($"{Webhook}/sonarr", new JsonObject
        {
            ["eventType"] = "Download",
            ["series"] = new JsonObject { ["title"] = "Test Show" },
            ["episodes"] = new JsonArray(
                new JsonObject { ["id"] = 9, ["seasonNumber"] = 1, ["episodeNumber"] = 2, ["title"] = "Hello" }),
            ["episodeFile"] = new JsonObject { ["path"] = "/media/t/x.mkv" },
        });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal("ignored", (string)response.Fields["status"]!);
        Assert.Equal("imported", (string)response.Fields["event"]!);
        Assert.Equal(before, (await RemuxJobs.ListAsync(admin)).Count);
    }

    [Fact]
    public async Task Radarr_non_download_is_ignored()
    {
        using var anonymous = Server.CreateClient();

        var response = await anonymous.PostAsync(
            $"{Webhook}/radarr", new JsonObject { ["eventType"] = "Grab", ["movie"] = new JsonObject { ["id"] = 1 } });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal("ignored", (string)response.Fields["status"]!);
    }

    [Fact]
    public async Task Radarr_download_is_accepted_and_ignored()
    {
        using var admin = await Server.CreateAdminClientAsync();
        var before = (await RemuxJobs.ListAsync(admin)).Count;

        var response = await admin.PostAsync($"{Webhook}/radarr", new JsonObject
        {
            ["eventType"] = "Download",
            ["movie"] = new JsonObject { ["id"] = 3, ["title"] = "Film", ["year"] = 2010 },
            ["movieFile"] = new JsonObject { ["path"] = "/media/m/f.mkv" },
        });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal("ignored", (string)response.Fields["status"]!);
        Assert.Equal(before, (await RemuxJobs.ListAsync(admin)).Count);
    }

    // --- the hand-off path, which is what a manager like Deluno needs -------------

    private static JsonObject Handoff(string handoffId, string mediaType, string sourcePath) => new()
    {
        ["eventType"] = "deluno.processor-handoff",
        ["handoffId"] = handoffId,
        ["mediaType"] = mediaType,
        ["sourcePath"] = sourcePath,
    };

    [Fact]
    public async Task Deluno_handoff_enqueues_a_processing_pass_with_a_relative_path()
    {
        using var client = await fixture.WithWatchedFoldersAsync();

        var response = await client.PostAsync($"{Webhook}/deluno", new JsonObject
        {
            ["eventType"] = "deluno.processor-handoff",
            ["handoffId"] = "handoff-1",
            ["libraryId"] = "lib-1",
            ["mediaType"] = "movies",
            ["sourcePath"] = Path.Combine(fixture.Movies.Watched, "Blade.Runner.2049", "film.mkv"),
            ["releaseName"] = "Blade.Runner.2049",
            ["callbackPath"] = RemuxJobs.CallbackPath,
        });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal("handoff", (string)response.Fields["event"]!);
        Assert.Equal(RemuxJobs.Kind, (string)response.Fields["enqueued"]!);
        var job = Assert.Single(await RemuxJobs.ForHandoffAsync(client, "handoff-1"));
        var payload = RemuxJobs.Payload(job);
        Assert.Equal("Blade.Runner.2049/film.mkv", (string)payload["relative_media_path"]!);
        Assert.Equal("movie", (string)payload["media_scope"]!);
        Assert.Equal("handoff-1", (string)payload["origin"]!["handoff_id"]!);
        Assert.Equal(RemuxJobs.CallbackPath, (string)payload["origin"]!["callback_path"]!);
        // Deluno refuses a processor event that does not name its library, so the id has to
        // survive from the hand-off to the report.
        Assert.Equal("lib-1", (string)payload["origin"]!["library_id"]!);
        Assert.Equal("webhook", (string)payload["trigger"]!);
    }

    [Fact]
    public async Task Repeated_handoff_id_does_not_remux_the_file_twice()
    {
        using var client = await fixture.WithWatchedFoldersAsync();
        var body = Handoff("handoff-same", "movies", Path.Combine(fixture.Movies.Watched, "Repeat", "film.mkv"));
        var before = (await RemuxJobs.ListAsync(client)).Count;

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Webhook}/deluno", body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Webhook}/deluno", body)).Status);

        Assert.Single(await RemuxJobs.ForHandoffAsync(client, "handoff-same"));
        Assert.Equal(before + 1, (await RemuxJobs.ListAsync(client)).Count);
    }

    [Fact]
    public async Task Handoff_outside_the_watched_folder_is_refused_with_a_plain_reason()
    {
        using var client = await fixture.WithWatchedFoldersAsync();
        using var elsewhere = new TemporaryFolder();
        var before = (await RemuxJobs.ListAsync(client)).Count;

        var response = await client.PostAsync(
            $"{Webhook}/deluno", Handoff("handoff-2", "movies", Path.Combine(elsewhere.Path, "somewhere", "else", "film.mkv")));

        Assert.True(response.Status == HttpStatusCode.BadRequest, response.ToString());
        var detail = (string)response.Fields["detail"]!;
        Assert.StartsWith("No Weir workflow watches the folder that 'film.mkv' is in. Weir's workflows: ", detail, StringComparison.Ordinal);
        Assert.Contains(" watches '", detail, StringComparison.Ordinal);
        Assert.EndsWith("or add a path mapping in the media manager.", detail, StringComparison.Ordinal);
        Assert.Equal(before, (await RemuxJobs.ListAsync(client)).Count);
    }

    [Fact]
    public async Task Handoff_without_a_configured_watched_folder_says_so()
    {
        // A server of its own: the shared server has watched folders once any hand-off test has run.
        await using var fresh = await WeirServer.StartNewAsync(ManagerEnvironment.NoWebhookSecret);
        using var client = await fresh.CreateAdminClientAsync();
        using var elsewhere = new TemporaryFolder();
        var created = await ManagerConnections.CreateAsync(client);
        Assert.Equal(HttpStatusCode.Created, created.Status);

        var response = await client.PostAsync(
            $"{Webhook}/deluno", Handoff("handoff-3", "tv", Path.Combine(elsewhere.Path, "handoff", "tv", "Show", "ep.mkv")));

        Assert.True(response.Status == HttpStatusCode.BadRequest, response.ToString());
        var detail = (string)response.Fields["detail"]!;
        Assert.StartsWith("No Weir workflow watches the folder that 'ep.mkv' is in. Weir's workflows: ", detail, StringComparison.Ordinal);
        Assert.Contains("has no watched folder yet", detail, StringComparison.Ordinal);
    }

    // --- the native shape, for a manager with no dialect of its own ---------------

    [Fact]
    public async Task Native_imported_event_is_accepted_and_ignored()
    {
        using var admin = await Server.CreateAdminClientAsync();
        await using var native = await NativeConnection.CreateAsync(admin);
        var before = (await RemuxJobs.ListAsync(admin)).Count;

        var response = await admin.PostAsync(
            $"{Webhook}/native",
            new JsonObject
            {
                ["event"] = "imported",
                ["mediaScope"] = "movie",
                ["filePath"] = "/media/m/x.mkv",
                ["title"] = "X",
                ["year"] = 1999,
            },
            native.SecretHeaders);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal("imported", (string)response.Fields["event"]!);
        Assert.Equal("ignored", (string)response.Fields["status"]!);
        Assert.Equal(before, (await RemuxJobs.ListAsync(admin)).Count);
    }

    [Fact]
    public async Task Native_handoff_event_enqueues_a_processing_pass()
    {
        using var client = await fixture.WithWatchedFoldersAsync();
        await using var native = await NativeConnection.CreateAsync(client);

        var response = await client.PostAsync(
            $"{Webhook}/native",
            new JsonObject
            {
                ["event"] = "handoff",
                ["mediaScope"] = "movie",
                ["filePath"] = Path.Combine(fixture.Movies.Watched, "Native", "x.mkv"),
                ["handoffId"] = "native-1",
            },
            native.SecretHeaders);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Single(await RemuxJobs.ForHandoffAsync(client, "native-1", sourceKey: "native"));
    }

    [Fact]
    public async Task Native_event_missing_required_fields_is_ignored()
    {
        using var admin = await Server.CreateAdminClientAsync();
        await using var native = await NativeConnection.CreateAsync(admin);

        var response = await admin.PostAsync(
            $"{Webhook}/native", new JsonObject { ["event"] = "imported", ["title"] = "no path" }, native.SecretHeaders);

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal("ignored", (string)response.Fields["status"]!);
    }

    [Fact]
    public async Task Native_source_with_no_secret_refuses_writes()
    {
        // A fresh server, so no earlier test's native connection secret is still on file.
        await using var fresh = await WeirServer.StartNewAsync(ManagerEnvironment.NoWebhookSecret);
        using var client = fresh.CreateClient();

        var response = await client.PostAsync(
            $"{Webhook}/native", new JsonObject { ["event"] = "imported", ["mediaScope"] = "movie", ["filePath"] = "/x.mkv" });

        Assert.True(response.Status == HttpStatusCode.Unauthorized, response.ToString());
        Assert.Equal("Set a webhook secret before sending hand-offs to Weir.", (string)response.Fields["detail"]!);
    }

    // --- the endpoint itself -----------------------------------------------------

    [Fact]
    public async Task Unknown_source_names_the_ones_that_exist()
    {
        using var anonymous = Server.CreateClient();

        var response = await anonymous.PostAsync($"{Webhook}/plex", new JsonObject());

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        var detail = (string)response.Fields["detail"]!;
        Assert.Contains("plex", detail);
        foreach (var known in new[] { "deluno", "native", "radarr", "sonarr" })
        {
            Assert.Contains(known, detail);
        }
    }

    [Fact]
    public async Task Source_key_is_case_insensitive()
    {
        using var anonymous = Server.CreateClient();

        var response = await anonymous.PostAsync($"{Webhook}/RADARR", new JsonObject { ["eventType"] = "Grab" });

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.Equal("ignored", (string)response.Fields["status"]!);
    }

    [Fact]
    public async Task A_manager_kind_with_no_connection_at_all_is_refused()
    {
        await using var fresh = await WeirServer.StartNewAsync(ManagerEnvironment.NoWebhookSecret);
        using var client = fresh.CreateClient();
        var grab = new JsonObject { ["eventType"] = "Grab" };

        var refused = await client.PostAsync($"{Webhook}/radarr", grab);
        Assert.True(refused.Status == HttpStatusCode.Unauthorized, refused.ToString());
        Assert.Equal(
            "No Radarr is set up in Weir. Add it in Setup › Connections › Media managers, then send webhooks with its secret.",
            (string)refused.Fields["detail"]!);

        await client.EnsureAdminAsync();
        var created = await ManagerConnections.CreateAsync(
            client, new JsonObject { ["kind"] = "radarr", ["name"] = "Radarr", ["base_url"] = "http://192.0.2.20:7878" });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());

        // An existing connection that has never rotated its secret still accepts an unsigned webhook.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Webhook}/radarr", grab)).Status);

        var secret = (string)(await ManagerConnections.GenerateSecretAsync(client, JsonFields.Id(created.Fields)))["webhook_secret"]!;
        var signed = await client.PostAsync($"{Webhook}/radarr", grab, new Dictionary<string, string> { ["X-Webhook-Secret"] = secret });
        Assert.Equal(HttpStatusCode.OK, signed.Status);
    }

    [Fact]
    public async Task Configured_secret_is_required()
    {
        await using var fresh = await WeirServer.StartNewAsync(ManagerEnvironment.WebhookSecret("s3cret"));
        using var client = fresh.CreateClient();
        var body = new JsonObject { ["eventType"] = "Grab" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"{Webhook}/radarr", body)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync($"{Webhook}/radarr", body, new Dictionary<string, string> { ["X-Webhook-Secret"] = "wrong" })).Status);
        var accepted = await client.PostAsync($"{Webhook}/radarr", body, new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" });
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
    }

    [Fact]
    public async Task The_old_subber_env_name_no_longer_configures_the_secret()
    {
        // Only WEIR_MEDIA_MANAGER_WEBHOOK_SECRET configures the shared secret, not WEIR_SUBBER_WEBHOOK_SECRET.
        // With two spellings a 401 could come from either one being wrong. Setting only the other name leaves
        // the webhook with no instance-wide secret at all, which is what this asserts: not a stricter check,
        // just no secret configured.
        await using var fresh = await WeirServer.StartNewAsync(
            ManagerEnvironment.NoWebhookSecret.With(("WEIR_SUBBER_WEBHOOK_SECRET", "s3cret")));
        using var client = fresh.CreateClient();
        await client.EnsureAdminAsync();
        var created = await ManagerConnections.CreateAsync(
            client, new JsonObject { ["kind"] = "radarr", ["name"] = "Radarr", ["base_url"] = "http://192.0.2.20:7878" });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Webhook}/radarr", new JsonObject { ["eventType"] = "Grab" })).Status);
    }
}
