using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// Basic contract coverage for Processing files, requeue, the file log and why-held: auth, viewer versus
/// operator, status codes, response keys, filters.
/// </summary>
[ContractArea("libraries")]
public sealed class ProcessingEndpointsContractTests(ProcessingEndpointsContractTests.FilesFixture fixture)
    : IClassFixture<ProcessingEndpointsContractTests.FilesFixture>
{
    private const string Files = Api + "/processing/files";

    private static readonly string[] FileKeys =
    [
        "id",
        "library_id",
        "library_name",
        "relative_path",
        "status",
        "status_reason",
        "blocked_by_connection",
        "size_bytes",
        "video_codec",
        "video_width",
        "video_height",
        "audio_track_count",
        "subtitle_track_count",
        "duration_seconds",
        "direct_play",
        "progress_percent",
        "progress_message",
        "progress_eta_seconds",
        "failure_class",
        "failure_attempts",
        "next_retry_at",
        "output_collision_policy",
        "output_collision_action",
        "output_collision_reason",
        "hold_until",
        "size_changed_at",
        "created_at",
        "updated_at",
        "last_seen_at",
        "last_attempt_at",
    ];

    private static void AssertHasKeys(JsonObject body, IEnumerable<string> keys)
    {
        var missing = keys.Where(key => !body.ContainsKey(key)).ToList();
        Assert.True(missing.Count == 0, $"missing keys: {string.Join(", ", missing)} in {body.ToJsonString()}");
    }

    private static async Task<JsonObject> FileAsync(WeirClient client, string relativePath)
    {
        var response = await client.GetAsync(Files, ("path_contains", relativePath));
        response.ShouldBe(HttpStatusCode.OK);
        return Assert.Single(
            response.Fields["files"]!.AsArray(),
            file => (string)file!["relative_path"]! == relativePath)!.AsObject();
    }

    private static List<string> RelativePaths(WeirResponse response) =>
        response.Fields["files"]!.AsArray().Select(file => (string)file!["relative_path"]!).ToList();

    // --- files ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Files_require_a_session()
    {
        using var client = fixture.Server.CreateClient();

        (await client.GetAsync(Files)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Files_list_shape()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var response = await viewer.GetAsync(Files);
        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;
        AssertHasKeys(body, ["files", "status_counts", "returned", "limit"]);
        Assert.Equal(200, (int)body["limit"]!);
        var files = body["files"]!.AsArray();
        Assert.Equal(files.Count, (int)body["returned"]!);
        Assert.True(files.Count >= 6, $"expected at least 6 files, got {files.Count}");
        Assert.IsType<JsonObject>(body["status_counts"]);
        AssertHasKeys(files[0]!.AsObject(), FileKeys);

        var alpha = await FileAsync(viewer, "Alpha/alpha.mkv");
        Assert.Equal("Movies", (string)alpha["library_name"]!);
        Assert.Equal("on_hold", (string)alpha["status"]!);
        Assert.Equal("Held for the test.", (string)alpha["status_reason"]!);
        Assert.Equal(4096, (long)alpha["size_bytes"]!);
        Assert.Equal(0, (int)alpha["failure_attempts"]!);
        Assert.False(alpha.ContainsKey("quarantined"));
        Assert.Empty(alpha["direct_play"]!.AsArray());
    }

    [Fact]
    public async Task Files_filters()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var libraries = (await admin.GetAsync($"{Api}/processing/libraries")).Elements
            .ToDictionary(row => (string)row!["name"]!, row => (long)row!["id"]!);

        var byLibrary = (await admin.GetAsync(Files, ("library_id", libraries["TV"]))).Fields;
        Assert.NotEmpty(byLibrary["files"]!.AsArray());
        Assert.All(byLibrary["files"]!.AsArray(), file => Assert.Equal(libraries["TV"], (long)file!["library_id"]!));

        var processed = await admin.GetAsync(Files, ("file_status", "processed"));
        Assert.Equal(["Beta/beta.mkv"], RelativePaths(processed));

        var byPath = await admin.GetAsync(Files, ("path_contains", "Alpha"));
        Assert.Equal(["Alpha/alpha.mkv"], RelativePaths(byPath));

        // within_days filters on when a scan last saw the file, or on when the file was recorded if no scan has: Show/S01E01.mkv was recorded just now.
        var recent = RelativePaths(await admin.GetAsync(Files, ("within_days", 30))).ToHashSet();
        Assert.Contains("Alpha/alpha.mkv", recent);
        Assert.DoesNotContain("Beta/beta.mkv", recent);
        Assert.Contains("Show/S01E01.mkv", recent);

        var limited = (await admin.GetAsync(Files, ("limit", 1))).Fields;
        Assert.Equal(1, (int)limited["returned"]!);
        Assert.Equal(1, (int)limited["limit"]!);
        Assert.Single(limited["files"]!.AsArray());
    }

    [Theory]
    [InlineData("file_status", "not_a_status")]
    [InlineData("limit", 0)]
    [InlineData("limit", 1001)]
    [InlineData("library_id", 0)]
    [InlineData("within_days", 0)]
    public async Task Files_reject_invalid_filters(string name, object value)
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        (await admin.GetAsync(Files, (name, value))).ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Requeue_one_file()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var target = await FileAsync(admin, "Requeue/one.mkv");
        var path = $"{Files}/{(long)target["id"]!}/requeue";

        using var anonymous = fixture.Server.CreateClient();
        (await anonymous.PostWithCsrfAsync(path)).ShouldBe(HttpStatusCode.Unauthorized);
        (await viewer.PostWithCsrfAsync(path)).ShouldBe(HttpStatusCode.Forbidden);
        (await admin.PostWithCsrfAsync($"{Files}/999999/requeue")).ShouldBe(HttpStatusCode.NotFound);

        var response = await admin.PostWithCsrfAsync(path);
        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;
        Assert.Equal(1, (int)body["requeued"]!);
        Assert.Equal(0, (int)body["skipped"]!);
        Assert.False(string.IsNullOrEmpty((string?)body["detail"]));

        var after = await FileAsync(admin, "Requeue/one.mkv");
        Assert.Equal("unprocessed", (string)after["status"]!);
        Assert.Equal(0, (int)after["failure_attempts"]!);
        AssertNullField(after, "failure_class");
        Assert.Equal((string)body["detail"]!, (string)after["status_reason"]!);
    }

    [Fact]
    public async Task Requeue_without_a_csrf_token_is_refused()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var target = await FileAsync(admin, "Requeue/one.mkv");
        var path = $"{Files}/{(long)target["id"]!}/requeue";

        (await admin.PostAsync(path, new JsonObject())).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await admin.PostAsync(path, Obj(("csrf_token", "forged")))).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bulk_requeue()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var bulk = $"{Files}/requeue";
        (await viewer.PostWithCsrfAsync(bulk, Obj(("path_contains", "BulkRequeue")))).ShouldBe(HttpStatusCode.Forbidden);

        var nothing = await admin.PostWithCsrfAsync(bulk, Obj(("path_contains", "no-file-has-this-in-its-path")));
        nothing.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(0, (int)nothing.Fields["requeued"]!);
        Assert.Equal(0, (int)nothing.Fields["skipped"]!);

        var response = await admin.PostWithCsrfAsync(
            bulk, Obj(("path_contains", "BulkRequeue"), ("file_status", "processing_failed")));
        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(2, (int)response.Fields["requeued"]!);
        Assert.Equal(0, (int)response.Fields["skipped"]!);
        foreach (var relativePath in new[] { "BulkRequeue/x1.mkv", "BulkRequeue/x2.mkv" })
        {
            Assert.Equal("unprocessed", (string)(await FileAsync(admin, relativePath))["status"]!);
        }
    }

    [Fact]
    public async Task Rejected_files_summary_counts_what_can_be_processed_again()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var response = await viewer.GetAsync($"{Files}/rejected/summary");
        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;

        Assert.Equal(new[] { "rejected", "ready" }.Order(), body.Select(pair => pair.Key).Order());
        var ready = (int)body["ready"]!;
        Assert.InRange(ready, 0, (int)body["rejected"]!);
        (await viewer.GetAsync($"{Files}/rejected/summary", ("library_id", 0))).ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Processing_all_rejected_files_again_is_for_operators_with_a_csrf_token()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var again = $"{Files}/rejected/process-again";

        (await viewer.PostWithCsrfAsync(again)).ShouldBe(HttpStatusCode.Forbidden);
        (await admin.PostAsync(again, new JsonObject())).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await admin.PostAsync(again, Obj(("csrf_token", "forged")))).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task File_log()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        (await viewer.GetAsync($"{Files}/999999/log")).ShouldBe(HttpStatusCode.NotFound);

        var alpha = await FileAsync(viewer, "Alpha/alpha.mkv");
        var response = await viewer.GetAsync($"{Files}/{(long)alpha["id"]!}/log");
        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;
        Assert.Equal((long)alpha["id"]!, (long)body["file_id"]!);
        Assert.Equal("Alpha/alpha.mkv", (string)body["relative_path"]!);
        Assert.True(IsInteger(body["retention_days"]));
        var entry = Assert.Single(body["entries"]!.AsArray())!.AsObject();
        AssertHasKeys(entry, ["id", "recorded_at", "outcome", "title", "library_name", "detail", "story"]);
        Assert.Equal("failed", (string)entry["outcome"]!);
        Assert.Equal("Remux failed", (string)entry["title"]!);
        Assert.Equal("Movies", (string)entry["library_name"]!);
        Assert.Equal("boom", (string)entry["detail"]!["reason"]!);
        Assert.IsType<JsonArray>(entry["story"]);

        var beta = await FileAsync(viewer, "Beta/beta.mkv");
        var empty = (await viewer.GetAsync($"{Files}/{(long)beta["id"]!}/log")).Fields;
        Assert.Empty(empty["entries"]!.AsArray());
    }

    [Fact]
    public async Task File_log_needs_a_session()
    {
        using var client = fixture.Server.CreateClient();

        (await client.GetAsync($"{Files}/1/log")).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Why_held()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        using var client = fixture.Server.CreateClient();
        (await client.GetAsync($"{Files}/1/why-held")).ShouldBe(HttpStatusCode.Unauthorized);
        (await viewer.GetAsync($"{Files}/999999/why-held")).ShouldBe(HttpStatusCode.NotFound);

        var alpha = await FileAsync(viewer, "Alpha/alpha.mkv");
        var response = await viewer.GetAsync($"{Files}/{(long)alpha["id"]!}/why-held");
        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;
        AssertHasKeys(
            body,
            [
                "file_id",
                "relative_path",
                "library_name",
                "recorded_status",
                "recorded_reason",
                "verdict",
                "owned",
                "blocked_upstream",
                "blocked_by_connection",
                "queue_row_count",
                "managers_consulted",
                "managers_reporting",
                "managers_without_queue_signal",
                "reasons",
            ]);
        Assert.Equal((long)alpha["id"]!, (long)body["file_id"]!);
        Assert.Equal("Alpha/alpha.mkv", (string)body["relative_path"]!);
        Assert.Equal("Movies", (string)body["library_name"]!);
        Assert.Equal("on_hold", (string)body["recorded_status"]!);
        Assert.Equal("Held for the test.", (string)body["recorded_reason"]!);
        Assert.Contains((string)body["verdict"]!, new[] { "proceed", "wait_upstream", "not_held", "no_upstream_signal" });
        // No media manager is connected on this install, so nobody was asked.
        Assert.Equal(0, (int)body["managers_consulted"]!);
        Assert.IsType<JsonArray>(body["reasons"]);
    }

    /// <summary>A viewer, and file rows across both seeded libraries (seeded while the server is stopped).</summary>
    public sealed class FilesFixture : SeededServerFixture
    {
        protected override void Seed(SqliteConnection connection)
        {
            SeededAccounts.EnsureViewer(connection);
            var movies = LibraryIdNamed(connection, "Movies");
            var tv = LibraryIdNamed(connection, "TV");
            var alpha = InsertFile(
                connection, movies, "Alpha/alpha.mkv",
                ("status", "on_hold"), ("status_reason", "Held for the test."), ("size_bytes", 4096), ("last_seen_at", Now()));
            InsertFile(
                connection, movies, "Beta/beta.mkv",
                ("status", "processed"), ("last_seen_at", SeedSql.UtcText(DateTime.UtcNow - TimeSpan.FromDays(400))));
            InsertFile(connection, tv, "Show/S01E01.mkv", ("status", "skipped"));
            InsertFile(
                connection, tv, "Requeue/one.mkv",
                ("status", "processing_failed"), ("failure_class", "execution"), ("failure_attempts", 2));
            InsertFile(connection, movies, "BulkRequeue/x1.mkv", ("status", "processing_failed"));
            InsertFile(connection, movies, "BulkRequeue/x2.mkv", ("status", "processing_failed"));
            SeedSql.Execute(
                connection,
                "INSERT INTO file_logs (file_id, library_id, relative_path, library_name, outcome, title, detail_json, recorded_at) "
                    + "VALUES ($file, $library, $path, $name, $outcome, $title, $detail, $now)",
                ("$file", alpha), ("$library", movies), ("$path", "Alpha/alpha.mkv"), ("$name", "Movies"),
                ("$outcome", "failed"), ("$title", "Remux failed"),
                ("$detail", Obj(("outcome", "failed"), ("reason", "boom")).ToJsonString()), ("$now", Now()));
        }
    }
}
