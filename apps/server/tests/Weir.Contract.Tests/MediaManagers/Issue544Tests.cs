using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// A server with an instance-wide secret, so the hand-off test's webhook and status calls authenticate. It never masks the per-connection
/// secrets of the last test: those win whenever any enabled connection of the kind has its own secret.
/// </summary>
public sealed class Issue544Fixture() : ManagerServerFixture(ManagerEnvironment.WebhookSecret(Issue544Tests.InstanceSecret));

/// <summary>Media manager edge cases: non-JSON 2xx answers, exact hand-off ledger matching, and per-connection secrets.</summary>
[ContractArea("media_managers")]
public sealed class Issue544Tests(Issue544Fixture fixture) : IClassFixture<Issue544Fixture>
{
    public const string InstanceSecret = "issue-544-instance-secret";

    private const string Route = ManagerConnections.Route;

    private static readonly Dictionary<string, string> InstanceSecretHeader = new() { ["X-Webhook-Secret"] = InstanceSecret };

    private WeirServer Server => fixture.Server;

    /// <summary>A signed-in operator with no media manager connections left over.</summary>
    private async Task<WeirClient> OperatorAsync()
    {
        var admin = await Server.CreateAdminClientAsync();
        await ManagerConnections.ClearAsync(admin);
        return admin;
    }

    private static async Task<JsonObject> CreatedAsync(WeirClient client, JsonObject? overrides = null)
    {
        var response = await ManagerConnections.CreateAsync(client, overrides);
        Assert.True(response.Status == HttpStatusCode.Created, response.ToString());
        return response.Fields;
    }

    private static JsonObject Radarr(string name, string baseUrl, string? apiKey = null)
    {
        var body = new JsonObject { ["kind"] = "radarr", ["name"] = name, ["base_url"] = baseUrl };
        if (apiKey is not null)
        {
            body["api_key"] = apiKey;
        }

        return body;
    }

    // --- item 1: a manager answering 2xx with a body that is not JSON ----------------------------
    //
    // An HTML sign-in page from a reverse proxy, most often. It is classified as "did not give Weir the
    // answer it expected" with a plain message, like any other manager that answers oddly.

    [Fact]
    public async Task A_connection_test_classifies_a_2xx_non_json_answer()
    {
        using var admin = await OperatorAsync();
        using var notAManager = new ManagerHtmlServer();
        var row = await CreatedAsync(admin, Radarr("Radarr", notAManager.BaseUrl, apiKey: "k"));

        var tested = await admin.PostWithCsrfAsync($"{Route}/{JsonFields.Id(row)}/test");

        Assert.True(tested.Status == HttpStatusCode.OK, tested.ToString());
        Assert.False((bool)tested.Fields["ok"]!);
        Assert.Contains("did not get the answer it expected", (string)tested.Fields["detail"]!);
    }

    [Fact]
    public async Task Capabilities_classifies_a_2xx_non_json_answer()
    {
        using var admin = await OperatorAsync();
        using var notAManager = new ManagerHtmlServer();
        await CreatedAsync(admin, Radarr("Radarr", notAManager.BaseUrl, apiKey: "k"));

        var response = await admin.GetAsync($"{WeirClient.Api}/media-managers/capabilities");

        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.False((bool)response.Elements[0]!["reachable"]!);
    }

    // --- item 5: hand-off ledger prefix matching is exact, not a SQL wildcard ---------------------

    /// <summary>
    /// A hand-off folder named with a <c>_</c> must not match an unrelated sibling folder whose name merely resembles it
    /// (<c>_</c> standing for any one character in SQL <c>LIKE</c>, which also ignores case): the sibling's file is never folded
    /// into this hand-off's status.
    /// </summary>
    [Fact]
    public async Task Hand_off_ledger_prefix_matching_is_exact_not_a_sql_wildcard()
    {
        var movies = fixture.Library("issue_544_movies");
        const string handoffId = "h-544-item-5";
        using (var admin = await Server.CreateAdminClientAsync())
        {
            await ProcessingLibraries.EnsureAsync(admin, "Issue544Movies", "movie", movies);
            var folder = Path.Combine(movies.Watched, "Foo_Bar");
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "film.mkv"), "12345"u8.ToArray());

            var handedOff = await admin.PostAsync(
                Handoffs.WebhookPath,
                new JsonObject
                {
                    ["eventType"] = "deluno.processor-handoff",
                    ["handoffId"] = handoffId,
                    ["mediaType"] = "movies",
                    ["sourcePath"] = folder,
                },
                InstanceSecretHeader);
            Assert.True(handedOff.Status == HttpStatusCode.OK, handedOff.ToString());
        }

        await using (var database = await Server.StopForDatabaseAsync())
        {
            var ledger = SeedSql.Rows(
                database.Connection,
                "SELECT library_id FROM media_manager_handoffs WHERE source_key = 'deluno' AND handoff_id = $id",
                ("$id", handoffId));
            Assert.True(ledger.Count > 0, "no ledger row was recorded for the hand-off");
            // Under SQL LIKE, "Foo_Bar/%" wildcards the "_" and matches this unrelated sibling folder's
            // file too — it is "mid-import" (processing), so a buggy match changes the reported state.
            SeedSql.Execute(
                database.Connection,
                "INSERT INTO files (library_id, relative_path, status) VALUES ($library, 'FooXBar/other.mkv', 'processing')",
                ("$library", ledger[0]["library_id"]));
        }

        // Seeding restarts the server on a fresh port, so the status is read from the server's current address.
        using var manager = Server.CreateClient();
        var status = await manager.GetAsync(Handoffs.StatusPath(handoffId), InstanceSecretHeader);
        Assert.True(status.Status == HttpStatusCode.OK, status.ToString());
        Assert.True(
            (string)status.Fields["state"]! == "queued",
            "an unrelated sibling folder's file must not be folded into this hand-off's status");
    }

    // --- item 6: several enabled connections of one kind each authenticate with their own secret --

    /// <summary>
    /// The webhook once only checked the first enabled connection of a kind (by id), so a second Radarr (a 4K instance alongside a
    /// 1080p one, say) with its own secret could never authenticate. The presented secret is matched against every enabled connection
    /// of the kind.
    /// </summary>
    [Fact]
    public async Task Two_radarr_connections_each_authenticate_with_their_own_secret()
    {
        using var admin = await OperatorAsync();
        var row1080p = await CreatedAsync(admin, Radarr("1080p", "http://192.0.2.20:7878"));
        var row4K = await CreatedAsync(admin, Radarr("4K", "http://192.0.2.21:7878"));
        var secret1080p = (string)(await ManagerConnections.GenerateSecretAsync(admin, JsonFields.Id(row1080p)))["webhook_secret"]!;
        var secret4K = (string)(await ManagerConnections.GenerateSecretAsync(admin, JsonFields.Id(row4K)))["webhook_secret"]!;
        Assert.NotEqual(secret1080p, secret4K);
        var webhook = $"{WeirClient.Api}/intake/webhook/radarr";
        var grab = new JsonObject { ["eventType"] = "Grab" };

        var accepted1080p = await admin.PostAsync(webhook, grab, new Dictionary<string, string> { ["X-Webhook-Secret"] = secret1080p });
        Assert.True(accepted1080p.Status == HttpStatusCode.OK, accepted1080p.ToString());
        var accepted4K = await admin.PostAsync(webhook, grab, new Dictionary<string, string> { ["X-Webhook-Secret"] = secret4K });
        Assert.True(accepted4K.Status == HttpStatusCode.OK, accepted4K.ToString());

        var refused = await admin.PostAsync(webhook, grab, new Dictionary<string, string> { ["X-Webhook-Secret"] = "neither connections secret" });
        Assert.True(refused.Status == HttpStatusCode.Unauthorized, refused.ToString());
    }
}
