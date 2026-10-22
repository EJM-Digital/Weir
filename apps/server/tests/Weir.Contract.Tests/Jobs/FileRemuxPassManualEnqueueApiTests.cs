using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Jobs;

/// <summary>Manually queueing a remux pass for one file: the job payload, a missing watched folder, and pass-through.</summary>
[ContractArea("jobs")]
public sealed class FileRemuxPassManualEnqueueApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Enqueue = $"{WeirClient.Api}/processing/jobs/file-remux-pass/enqueue";

    private static JsonObject MediaPath(string path) => new() { ["relative_media_path"] = path };

    [Fact]
    public async Task Processing_file_remux_pass_enqueue_writes_live_payload()
    {
        using var folders = new TemporaryFolder();
        var watch = Directory.CreateDirectory(Path.Combine(folders.Path, "remux_watch")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(folders.Path, "remux_out")).FullName;
        using var admin = await fixture.Server.CreateAdminClientAsync();
        await JobsApi.SetMovieFoldersAsync(admin, watch, output);

        var response = await admin.PostWithCsrfAsync(Enqueue, MediaPath("movies/sample.mkv"));

        JobsApi.Expect(response, HttpStatusCode.OK);
        var body = response.Fields;
        Assert.True((bool)body["ok"]!);
        Assert.Equal(JobsApi.RemuxPassKind, (string)body["job_kind"]!);
        var row = await JobsApi.JobByIdAsync(admin, (int)body["job_id"]!);
        Assert.Equal(JobsApi.RemuxPassKind, (string)row["job_kind"]!);
        Assert.Equal("pending", (string)row["status"]!);
        var payload = (string?)row["payload_json"] ?? "";
        Assert.DoesNotContain("\"dry_run\"", payload);
        Assert.Contains("\"relative_media_path\":\"movies/sample.mkv\"", payload);
    }

    [Fact]
    public async Task Processing_file_remux_pass_enqueue_rejects_missing_watched_folder()
    {
        using var folders = new TemporaryFolder();
        var output = Directory.CreateDirectory(Path.Combine(folders.Path, "enqueue_out_only")).FullName;
        using var admin = await fixture.Server.CreateAdminClientAsync();
        await JobsApi.SetMovieFoldersAsync(admin, watched: null, output);

        var response = await admin.PostWithCsrfAsync(Enqueue, MediaPath("movies/sample.mkv"));

        JobsApi.Expect(response, HttpStatusCode.BadRequest);
        var detail = Assert.IsAssignableFrom<JsonValue>(response.Fields["detail"]).GetValue<string>();
        Assert.Equal(
            "Movies has no watched folder yet, so Weir cannot find that file. Set the watched folder on the Movies workflow, then try again.",
            detail);
    }

    [Fact]
    public async Task Pass_through_converts_an_existing_pending_job_instead_of_duplicating_it()
    {
        using var folders = new TemporaryFolder();
        var watch = Directory.CreateDirectory(Path.Combine(folders.Path, "pass_watch")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(folders.Path, "pass_out")).FullName;
        int jobId;
        using (var admin = await fixture.Server.CreateAdminClientAsync())
        {
            await JobsApi.SetMovieFoldersAsync(admin, watch, output);
            var first = await admin.PostWithCsrfAsync(Enqueue, MediaPath("Foreign/film.mkv"));
            JobsApi.Expect(first, HttpStatusCode.OK);
            jobId = (int)first.Fields["job_id"]!;
        }

        // A hand-off from a media manager adds its origin to the queued job; no API writes that, so
        // it is added to the row while the server is stopped. Other remux jobs are cleared so the count is exact.
        await using (var database = await fixture.Server.StopForDatabaseAsync())
        {
            SeedSql.Execute(
                database.Connection,
                "DELETE FROM jobs WHERE id != $id AND job_kind = $kind",
                ("$id", jobId), ("$kind", JobsApi.RemuxPassKind));
            var raw = (string?)SeedSql.Scalar(database.Connection, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", jobId));
            var queued = JsonNode.Parse(raw ?? "{}")!.AsObject();
            queued["origin"] = new JsonObject { ["source_key"] = "radarr", ["handoff_id"] = "handoff-1" };
            SeedSql.Execute(
                database.Connection,
                "UPDATE jobs SET payload_json = $payload WHERE id = $id",
                ("$payload", queued.ToJsonString()), ("$id", jobId));
        }

        using var client = fixture.Server.CreateClient();
        await client.LoginAsync();
        var request = MediaPath("Foreign/film.mkv");
        request["pass_through_unchanged"] = true;
        var second = await client.PostWithCsrfAsync(Enqueue, request);
        JobsApi.Expect(second, HttpStatusCode.OK);
        Assert.Equal(jobId, (int)second.Fields["job_id"]!);

        // Only remux-pass rows: a restart lets the periodic scheduler queue a folder scan for the
        // library's watched folder, which is not what this test is about.
        var rows = (await JobsApi.AllJobsAsync(client)).Where(row => (string)row["job_kind"]! == JobsApi.RemuxPassKind).ToList();
        var single = Assert.Single(rows);
        var payload = JsonNode.Parse((string?)single["payload_json"] ?? "{}")!.AsObject();
        Assert.True((bool)payload["pass_through_unchanged"]!);
        var expectedOrigin = new JsonObject { ["source_key"] = "radarr", ["handoff_id"] = "handoff-1" };
        Assert.True(JsonNode.DeepEquals(payload["origin"], expectedOrigin), payload.ToJsonString());
    }
}
