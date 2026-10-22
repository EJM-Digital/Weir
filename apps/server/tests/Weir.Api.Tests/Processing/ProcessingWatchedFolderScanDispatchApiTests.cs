using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// "Scan now": who may ask for a watched-folder scan, each reason Weir turns the request down (nothing saved, no output folder,
/// the folder is gone, Deluno hands the downloads over), and the job it queues when it accepts.
/// </summary>
public sealed class ProcessingWatchedFolderScanDispatchApiTests : IAsyncLifetime
{
    private const string Enqueue = "/api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue";
    private const string ScanKind = "processing.watched_folder.remux_scan_dispatch.v1";
    private const string ConfirmationExpired = "Your confirmation token expired. Refresh the page and try again.";

    private readonly string _folders = Path.Join(Path.GetTempPath(), "weir-scan-dispatch-" + Guid.NewGuid().ToString("N"));
    private WeirTestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(_server);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        if (Directory.Exists(_folders))
        {
            Directory.Delete(_folders, recursive: true);
        }
    }

    private string MakeFolder(string name) => Directory.CreateDirectory(Path.Join(_folders, name)).FullName;

    private Task SaveFoldersAsync(string mediaType, string? watched, string? output) =>
        TestDatabase.ExecuteAsync(
            _server,
            "UPDATE libraries SET watched_folder = $w, output_folder = $o WHERE media_type = $t",
            ("$w", watched ?? string.Empty), ("$o", output ?? string.Empty), ("$t", mediaType));

    private async Task<HttpResponseMessage> AskAsync(ApiTestClient client, object? extra = null)
    {
        var body = new JsonObject { ["csrf_token"] = await client.CsrfAsync() };
        if (extra is not null)
        {
            foreach (var (name, value) in JsonSerializer.SerializeToNode(extra)!.AsObject())
            {
                body[name] = value?.DeepClone();
            }
        }

        return await client.PostAsync(Enqueue, body);
    }

    private Task<long> QueuedScansAsync() => TestDatabase.ScalarAsync(_server, $"SELECT count(*) FROM jobs WHERE job_kind = '{ScanKind}'");

    [Fact]
    public async Task Only_an_operator_or_an_administrator_may_ask_for_a_scan()
    {
        await Callers.AssertWhoMayCallAsync(_server, HttpMethod.Post, Enqueue, Callers.Operator);

        Assert.Equal(0, await QueuedScansAsync());
    }

    [Fact]
    public async Task A_missing_or_wrong_confirmation_token_and_an_unknown_field_are_refused_and_queue_nothing()
    {
        var admin = await Callers.SignedInAsync(_server, Callers.Admin);
        await SaveFoldersAsync("movie", MakeFolder("films"), MakeFolder("films-out"));

        using var wrongToken = await admin.PostAsync(Enqueue, new { csrf_token = "not-the-session-token" });
        using var noToken = await admin.PostAsync(Enqueue, new { });
        using var unknownField = await AskAsync(admin, new { surprise = true });
        using var badScope = await AskAsync(admin, new { media_scope = "music" });
        using var badLibrary = await AskAsync(admin, new { library_id = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, wrongToken.StatusCode);
        Assert.Equal(ConfirmationExpired, await Detail(wrongToken));
        Assert.All([noToken, unknownField, badScope, badLibrary], response => Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        Assert.Equal(0, await QueuedScansAsync());
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task A_scan_with_nothing_else_said_looks_through_the_movies_folder_and_queues_remux_jobs(string role)
    {
        var caller = await Callers.SignedInAsync(_server, role);
        await SaveFoldersAsync("movie", MakeFolder("films"), MakeFolder("films-out"));

        using var response = await AskAsync(caller);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await Json(response);
        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal(ScanKind, reply["job_kind"]!.GetValue<string>());
        var jobId = reply["job_id"]!.GetValue<long>();
        Assert.StartsWith(ScanKind + ":", reply["dedupe_key"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(
            """{"enqueue_remux_jobs":true,"scan_trigger":"manual","media_scope":"movie"}""",
            await TestDatabase.ScalarStringAsync(_server, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", jobId)));
        Assert.Equal("pending", await TestDatabase.ScalarStringAsync(_server, "SELECT status FROM jobs WHERE id = $id", ("$id", jobId)));
    }

    [Fact]
    public async Task A_scan_that_only_looks_needs_no_output_folder_and_may_name_the_tv_scope_and_one_workflow()
    {
        var admin = await Callers.SignedInAsync(_server, Callers.Admin);
        await SaveFoldersAsync("tv", MakeFolder("shows"), null);
        var tvId = await TestDatabase.ScalarAsync(_server, "SELECT id FROM libraries WHERE media_type = 'tv'");

        using var response = await AskAsync(admin, new { enqueue_remux_jobs = false, media_scope = "tv", library_id = tvId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jobId = (await Json(response))["job_id"]!.GetValue<long>();
        Assert.Equal(
            $$"""{"enqueue_remux_jobs":false,"scan_trigger":"manual","media_scope":"tv","library_id":{{tvId}}}""",
            await TestDatabase.ScalarStringAsync(_server, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", jobId)));
    }

    [Fact]
    public async Task A_workflow_id_nobody_has_falls_back_to_the_first_workflow_of_the_scope()
    {
        var admin = await Callers.SignedInAsync(_server, Callers.Admin);
        await SaveFoldersAsync("movie", MakeFolder("films"), MakeFolder("films-out"));

        using var response = await AskAsync(admin, new { library_id = 9999 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await QueuedScansAsync());
    }

    [Theory]
    [InlineData("movie", "Movies")]
    [InlineData("tv", "TV")]
    public async Task A_scope_with_no_watched_folder_saved_says_which_one_to_set(string scope, string label)
    {
        var admin = await Callers.SignedInAsync(_server, Callers.Admin);
        await SaveFoldersAsync(scope, null, MakeFolder("out"));

        using var response = await AskAsync(admin, new { media_scope = scope });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            $"{label} watched folder is not set in saved path settings. This scan reads media files under that folder — configure it first.",
            await Detail(response));
        Assert.Equal(0, await QueuedScansAsync());
    }

    [Fact]
    public async Task A_scope_with_no_workflow_at_all_says_the_watched_folder_is_not_set()
    {
        var admin = await Callers.SignedInAsync(_server, Callers.Admin);
        await TestDatabase.ExecuteAsync(_server, "DELETE FROM libraries WHERE media_type = 'tv'");

        using var response = await AskAsync(admin, new { media_scope = "tv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("TV watched folder is not set", await Detail(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scan_that_would_queue_remux_jobs_needs_an_output_folder_but_one_that_only_looks_does_not()
    {
        var admin = await Callers.SignedInAsync(_server, Callers.Admin);
        await SaveFoldersAsync("movie", MakeFolder("films"), null);

        using var refused = await AskAsync(admin);
        using var accepted = await AskAsync(admin, new { enqueue_remux_jobs = false });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("enqueue_remux_jobs requires a saved output folder for this media scope.", await Detail(refused));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, await QueuedScansAsync());
    }

    [Fact]
    public async Task A_watched_folder_that_is_not_on_disk_is_named_in_the_refusal()
    {
        var admin = await Callers.SignedInAsync(_server, Callers.Admin);
        var gone = Path.Join(_folders, "disconnected");
        await SaveFoldersAsync("movie", gone, MakeFolder("films-out"));

        using var response = await AskAsync(admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await Detail(response);
        Assert.Contains($"Weir can't see the watched folder {gone}", detail, StringComparison.Ordinal);
        Assert.Equal(0, await QueuedScansAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_workflow_that_deluno_hands_downloads_to_is_never_scanned_by_asking_for_the_scope_or_the_workflow(bool byLibraryId)
    {
        var admin = await Callers.SignedInAsync(_server, Callers.Admin);
        await SaveFoldersAsync("movie", MakeFolder("films"), MakeFolder("films-out"));
        var connection = await TestDatabase.ScalarAsync(
            _server,
            "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('deluno', 'Deluno', 'http://192.0.2.10:5099') RETURNING id");
        await TestDatabase.ExecuteAsync(
            _server,
            "INSERT INTO library_manager_links (library_id, connection_id) SELECT id, $c FROM libraries WHERE media_type = 'movie'",
            ("$c", connection));
        var movieId = await TestDatabase.ScalarAsync(_server, "SELECT id FROM libraries WHERE media_type = 'movie'");

        using var response = byLibraryId ? await AskAsync(admin, new { library_id = movieId }) : await AskAsync(admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Deluno hands this workflow its downloads, so Weir does not scan its watched folder.", await Detail(response));
        Assert.Equal(0, await QueuedScansAsync());
    }
}
