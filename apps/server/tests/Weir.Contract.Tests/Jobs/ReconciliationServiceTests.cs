using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>Reconciliation: the report and its safe repairs, over HTTP.</summary>
[ContractArea("jobs")]
public sealed class ReconciliationServiceTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Report = $"{WeirClient.Api}/system/reconciliation";
    private const string Repair = $"{Report}/repair";
    private const string TrustedOrigin = "http://127.0.0.1:9000";
    private const string ConfirmHint = "confirm=true";

    private static JsonObject RepairBody() => new()
    {
        ["action"] = "remove_processing_temp_artifact",
        ["path"] = "/nowhere/.x.partial",
        ["confirm"] = false,
    };

    [Fact]
    public async Task Reconciliation_temp_artifact_repair_requires_confirmation()
    {
        using var folders = new TemporaryFolder();
        var work = Directory.CreateDirectory(Path.Combine(folders.Path, "work")).FullName;
        var artifact = Path.Combine(work, ".movie.mkv.partial");
        await File.WriteAllBytesAsync(artifact, "partial"u8.ToArray());
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var movies = await JobsApi.LibraryForScopeAsync(admin, "movie");
        var moviesId = (int)movies["id"]!;
        await JobsApi.SaveLibraryAsync(admin, moviesId, ("watched_folder", ""), ("output_folder", ""), ("work_folder", work));

        try
        {
            var report = await admin.GetAsync(Report);
            JobsApi.Expect(report, HttpStatusCode.OK);
            var issue = report.Fields["issues"]!.AsArray()
                .Select(item => item!.AsObject())
                .First(item => (string)item["kind"]! == "partial_temp_artifact");

            // A repair needs a CSRF token (#527).
            var refused = await admin.PostWithCsrfAsync(Repair, new JsonObject
            {
                ["action"] = issue["repair_action"]!.DeepClone(),
                ["path"] = issue["path"]!.DeepClone(),
                ["confirm"] = false,
            });
            JobsApi.Expect(refused, HttpStatusCode.BadRequest);
            Assert.Contains(ConfirmHint, (string)refused.Fields["detail"]!);
            Assert.True(File.Exists(artifact));

            var applied = await admin.PostWithCsrfAsync(Repair, new JsonObject
            {
                ["action"] = issue["repair_action"]!.DeepClone(),
                ["path"] = issue["path"]!.DeepClone(),
                ["confirm"] = true,
            });
            JobsApi.Expect(applied, HttpStatusCode.OK);
            Assert.True((bool)applied.Fields["applied"]!);
            Assert.False(File.Exists(artifact));
        }
        finally
        {
            await JobsApi.SaveLibraryAsync(
                admin, moviesId, ("watched_folder", ""), ("output_folder", ""), ("work_folder", ""));
        }
    }

    [Fact]
    public async Task Reconciliation_report_rejects_viewer()
    {
        (await fixture.Server.CreateAdminClientAsync()).Dispose();

        await using (var database = await fixture.Server.StopForDatabaseAsync())
        {
            SeededAccounts.EnsureViewer(database.Connection);
        }

        using var viewer = fixture.Server.CreateClient();
        await viewer.LoginAsync(SeededAccounts.ViewerUsername, SeededAccounts.ViewerPassword);
        JobsApi.Expect(await viewer.GetAsync(Report), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reconciliation_report_allows_admin()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        var response = await admin.GetAsync(Report);

        JobsApi.Expect(response, HttpStatusCode.OK);
        Assert.True(response.Fields.ContainsKey("issues"));
    }

    [Fact]
    public async Task Reconciliation_repair_requires_a_csrf_token()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        var missing = await admin.PostAsync(Repair, RepairBody());
        JobsApi.Expect(missing, HttpStatusCode.BadRequest);
        var expected = new JsonObject { ["detail"] = "Your confirmation token expired. Refresh the page and try again." };
        Assert.True(JsonNode.DeepEquals(missing.Json, expected), missing.ToString());

        var body = RepairBody();
        body["csrf_token"] = "not-a-token";
        var wrong = await admin.PostAsync(Repair, body);
        JobsApi.Expect(wrong, HttpStatusCode.BadRequest);

        // A valid token reaches the repair itself, which refuses without confirmation.
        var valid = await admin.PostWithCsrfAsync(Repair, RepairBody());
        JobsApi.Expect(valid, HttpStatusCode.BadRequest);
        Assert.Contains(ConfirmHint, (string)valid.Fields["detail"]!);
    }

    [Fact]
    public async Task Reconciliation_repair_checks_the_browser_origin()
    {
        await using var server = await WeirServer.StartNewAsync(
            new Dictionary<string, string> { ["WEIR_TRUSTED_BROWSER_ORIGINS"] = TrustedOrigin });
        using var trusted = await server.CreateAdminClientAsync(new Dictionary<string, string>
        {
            ["Origin"] = TrustedOrigin,
            ["X-Requested-With"] = "XMLHttpRequest",
        });
        var token = await trusted.CsrfTokenAsync();

        var body = RepairBody();
        body["csrf_token"] = token;
        var evil = await trusted.PostAsync(
            Repair, body, new Dictionary<string, string> { ["Origin"] = "http://evil.test" });
        JobsApi.Expect(evil, HttpStatusCode.Forbidden);
        var expected = new JsonObject { ["detail"] = "Origin not allowed." };
        Assert.True(JsonNode.DeepEquals(evil.Json, expected), evil.ToString());

        var allowedBody = RepairBody();
        allowedBody["csrf_token"] = token;
        var allowed = await trusted.PostAsync(Repair, allowedBody);
        JobsApi.Expect(allowed, HttpStatusCode.BadRequest);
        Assert.Contains(ConfirmHint, (string)allowed.Fields["detail"]!);
    }
}
