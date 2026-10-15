using System.Net;
using Weir.Api.Tests.Platform;
using Weir.Core.Jobs;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// "Recover" on a result whose media work finished but whose last step failed: Weir marks the job completed and does not run it again,
/// so the file it already wrote is left exactly as it is. A job in any other state is refused.
/// </summary>
public sealed class RecoverFinalizeFailedApiTests
{
    private const string Kind = "processing.file.remux_pass.v1";
    private const string FinalizeError = "finalize failed: could not move the finished file into the output folder";

    private static string RecoverPath(long jobId) => $"/api/v1/processing/jobs/{jobId}/recover-finalize-failed";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static Task<long> SeedJobAsync(WeirTestServer server, string status, string key = "recover-me") =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, attempt_count, last_error) " +
            "VALUES ($key, $kind, '{\"relative_media_path\": \"Heat/heat.mkv\"}', $status, 2, $error) RETURNING id",
            ("$key", $"{Kind}:{key}"), ("$kind", Kind), ("$status", status), ("$error", FinalizeError));

    private static async Task<HttpResponseMessage> RecoverAsync(ApiTestClient client, long jobId) =>
        await client.PostAsync(RecoverPath(jobId), new { csrf_token = await client.CsrfAsync() });

    private static Task<string?> StatusAsync(WeirTestServer server, long jobId) =>
        TestDatabase.ScalarStringAsync(server, "SELECT status FROM jobs WHERE id = $id", ("$id", jobId));

    [Fact]
    public async Task A_job_whose_last_step_failed_is_marked_completed_and_its_output_is_not_written_again()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var output = Path.Join(Directory.CreateDirectory(Path.Join(server.Home, "output")).FullName, "heat.mkv");
        await File.WriteAllTextAsync(output, "already written by the first run");
        var writtenAt = File.GetLastWriteTimeUtc(output);
        var jobId = await SeedJobAsync(server, ProcessingJobStatus.HandlerOkFinalizeFailed);

        using var response = await RecoverAsync(client, jobId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal(jobId, body["job_id"]!.GetValue<long>());
        Assert.Equal(ProcessingJobStatus.Completed, body["status"]!.GetValue<string>());
        Assert.Equal(ProcessingJobStatus.Completed, await StatusAsync(server, jobId));
        var note = await TestDatabase.ScalarStringAsync(server, "SELECT last_error FROM jobs WHERE id = $id", ("$id", jobId));
        Assert.StartsWith(FinalizeError + "\n--- manual_recover_finalize_failure: marked completed at ", note, StringComparison.Ordinal);
        Assert.EndsWith(" by alice (handler was not re-run; row was handler_ok_finalize_failed).", note, StringComparison.Ordinal);
        Assert.Equal(2, await TestDatabase.ScalarAsync(server, "SELECT attempt_count FROM jobs WHERE id = $id", ("$id", jobId)));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = $kind", ("$kind", Kind)));
        Assert.Equal("already written by the first run", await File.ReadAllTextAsync(output));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(output));
    }

    [Theory]
    [InlineData(ProcessingJobStatus.Pending)]
    [InlineData(ProcessingJobStatus.Leased)]
    [InlineData(ProcessingJobStatus.Completed)]
    [InlineData(ProcessingJobStatus.Failed)]
    [InlineData(ProcessingJobStatus.Cancelled)]
    public async Task A_job_in_any_other_state_is_refused_and_left_as_it_was(string status)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var jobId = await SeedJobAsync(server, status);

        using var response = await RecoverAsync(client, jobId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Only a job whose media work completed but finalization failed can be recovered.", await Detail(response));
        Assert.Equal(status, await StatusAsync(server, jobId));
        Assert.Equal(FinalizeError, await TestDatabase.ScalarStringAsync(server, "SELECT last_error FROM jobs WHERE id = $id", ("$id", jobId)));
    }

    [Fact]
    public async Task A_job_that_does_not_exist_is_refused_with_not_found()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await RecoverAsync(client, 99999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Job not found.", await Detail(response));
    }

    [Fact]
    public async Task Without_an_operators_session_and_token_a_job_cannot_be_recovered()
    {
        var (server, _) = await StartAsync();
        await using var __ = server;
        var jobId = await SeedJobAsync(server, ProcessingJobStatus.HandlerOkFinalizeFailed);

        await OperatorPostRefusals.AssertAsync(server, RecoverPath(jobId), csrf => new { csrf_token = csrf });

        Assert.Equal(ProcessingJobStatus.HandlerOkFinalizeFailed, await StatusAsync(server, jobId));
    }
}
