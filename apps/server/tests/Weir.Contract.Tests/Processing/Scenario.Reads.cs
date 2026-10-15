using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

internal sealed partial class Scenario
{
    private static readonly string[] FinishedJobStatuses = ["completed", "failed", "handler_ok_finalize_failed", "cancelled"];

    private static readonly TimeSpan RetryScanSpacing = TimeSpan.FromSeconds(2);

    // --- hand-offs ------------------------------------------------------------------------------

    public async Task<JsonObject> HandoffStatusAsync(string handoffId)
    {
        var status = await Admin.GetAsync($"{Api}/intake/handoffs/deluno/{handoffId}", SecretHeader);
        Assert.True(status.Status == HttpStatusCode.OK, status.ToString());
        return status.Fields;
    }

    public async Task<JsonObject> WaitForHandoffStateAsync(string handoffId, string state, TimeSpan? timeout = null) =>
        await Poll.UntilAsync(
            async () =>
            {
                var status = await HandoffStatusAsync(handoffId);
                Assert.True((string)status["state"]! == state, status.ToJsonString());
                return status;
            },
            $"hand-off {handoffId} to reach '{state}'",
            timeout ?? TimeSpan.FromSeconds(90));

    /// <summary>The completion and failure reports the fake manager received for one hand-off.</summary>
    public static List<JsonObject> Callbacks(FakeManager fake, string handoffId) => fake.RequestsTo("POST", EventsPath)
        .Select(request => request.Json as JsonObject)
        .OfType<JsonObject>()
        .Where(report => (string?)report["handoffId"] == handoffId)
        .ToList();

    // --- jobs -----------------------------------------------------------------------------------

    public async Task<List<JsonObject>> JobsAsync(string? kind = null)
    {
        var inspection = await Admin.GetAsync($"{Api}/processing/jobs/inspection", ("limit", 100));
        Assert.True(inspection.Status == HttpStatusCode.OK, inspection.ToString());
        return JobRows(inspection).Where(row => kind is null || (string)row["job_kind"]! == kind).ToList();
    }

    /// <summary>The job's inspection row once it has left <c>pending</c> and <c>leased</c>.</summary>
    public async Task<JsonObject> WaitForJobFinishedAsync(int jobId, TimeSpan? timeout = null) =>
        await Poll.UntilAsync(
            async () =>
            {
                // Named explicitly: the default inspection list leaves out completed scan jobs.
                var inspection = await Admin.GetAsync(
                    $"{Api}/processing/jobs/inspection", [("limit", 100), .. FinishedJobStatuses.Select(status => ("status", (object)status))]);
                Assert.True(inspection.Status == HttpStatusCode.OK, inspection.ToString());
                return JobRows(inspection).FirstOrDefault(row => (int)row["id"]! == jobId);
            },
            $"job {jobId} to finish",
            timeout ?? TimeSpan.FromSeconds(60));

    public async Task<int> EnqueueScanAsync(JsonObject library, bool enqueueRemuxJobs = true)
    {
        var scan = await Admin.PostWithCsrfAsync(
            $"{Api}/processing/jobs/watched-folder-remux-scan-dispatch/enqueue",
            new JsonObject
            {
                ["media_scope"] = library["media_type"]!.DeepClone(),
                ["library_id"] = (int)library["id"]!,
                ["enqueue_remux_jobs"] = enqueueRemuxJobs,
            });
        Assert.True(scan.Status == HttpStatusCode.OK, scan.ToString());
        return (int)scan.Fields["job_id"]!;
    }

    // --- files ----------------------------------------------------------------------------------

    public async Task<JsonObject?> FileRowAsync(JsonObject library, string relativePath)
    {
        var listing = await Admin.GetAsync($"{Api}/processing/files", ("limit", 1000), ("library_id", (int)library["id"]!));
        Assert.True(listing.Status == HttpStatusCode.OK, listing.ToString());
        return listing.Fields["files"]!.AsArray().OfType<JsonObject>().FirstOrDefault(row => (string)row["relative_path"]! == relativePath);
    }

    public async Task<JsonObject> WaitForFileStatusAsync(JsonObject library, string relativePath, string status, TimeSpan? timeout = null) =>
        await Poll.UntilAsync(
            async () =>
            {
                var row = await FileRowAsync(library, relativePath);
                return row is not null && (string)row["status"]! == status ? row : null;
            },
            $"{relativePath} to reach status '{status}'",
            timeout ?? TimeSpan.FromSeconds(90));

    /// <summary>
    /// Lets a scan notice the file (recording its size) without queueing it, as a periodic scan would have. Scenarios that count
    /// retries use this so the file is known before the hand-off, as in normal use.
    /// </summary>
    public async Task<JsonObject> DetectWithoutQueueingAsync(JsonObject library, string relativePath)
    {
        await EnqueueScanAsync(library, enqueueRemuxJobs: false);
        return await Poll.UntilAsync(
            async () =>
            {
                var row = await FileRowAsync(library, relativePath);
                return row is not null && Whole(row["size_bytes"]) > 0 ? row : null;
            },
            $"a scan to record {relativePath}");
    }

    /// <summary>The <c>files</c> row as stored, read from SQLite with the server stopped (and started again).</summary>
    public async Task<Dictionary<string, object?>> FileStateAfterStopAsync(JsonObject library, string relativePath)
    {
        await using var database = await Server.StopForDatabaseAsync();
        var found = SeedSql.Rows(
            database.Connection,
            "SELECT * FROM files WHERE library_id = $library AND relative_path = $path",
            ("$library", (int)library["id"]!),
            ("$path", relativePath));
        Assert.Single(found);
        return found[0];
    }

    /// <summary>
    /// Rows read from SQLite with the server stopped. The server is started again, at a new address, and the scenario signs in to it,
    /// so the scenario goes on working afterwards.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> RowsAfterStopAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        List<Dictionary<string, object?>> rows;
        await using (var database = await Server.StopForDatabaseAsync())
        {
            rows = SeedSql.Rows(database.Connection, sql, parameters);
        }

        Admin.Dispose();
        Admin = Server.CreateClient();
        await Admin.LoginAsync();
        return rows;
    }

    public async Task<List<JsonObject>> ActivityAsync(string eventType)
    {
        var recent = await Admin.GetAsync($"{Api}/activity/recent", ("event_type", eventType), ("limit", 100));
        Assert.True(recent.Status == HttpStatusCode.OK, recent.ToString());
        return [.. recent.Fields["items"]!.AsArray().OfType<JsonObject>()];
    }

    // --- retries --------------------------------------------------------------------------------

    /// <summary>
    /// Lets automatic retries happen, as the periodic watched-folder scan would, until <paramref name="probe"/> says yes. A failed
    /// file is picked up again by a scan once its backoff has passed. Rather than wait out the scan timer, the scenario asks for a
    /// scan while a file waits for its retry and no scan is queued. A workflow a fake Deluno feeds is never scanned: its failed
    /// hand-off queues its own retry, so there the scenario only waits.
    /// </summary>
    public async Task DriveRetriesUntilAsync(JsonObject library, Func<Task<bool>> probe, string what, TimeSpan? timeout = null) =>
        await DriveRetriesUntilAsync(library, async () => await probe() ? "done" : null, what, timeout);

    public async Task<T> DriveRetriesUntilAsync<T>(JsonObject library, Func<Task<T?>> probe, string what, TimeSpan? timeout = null)
        where T : class
    {
        var sinceLastScan = new Stopwatch();
        return await Poll.UntilAsync(
            async () =>
            {
                if (await probe() is { } found)
                {
                    return found;
                }

                var waiting = (await FilesAsync(library)).Any(row => (string)row["status"]! == "processing_failed");
                if (waiting && (int)library["id"]! != HandedOffLibraryId && (!sinceLastScan.IsRunning || sinceLastScan.Elapsed >= RetryScanSpacing) && !await ScanPendingAsync())
                {
                    await EnqueueScanAsync(library);
                    sinceLastScan.Restart();
                }

                return null;
            },
            what,
            timeout ?? TimeSpan.FromSeconds(120));
    }

    /// <summary>Drives retries until the file has failed <paramref name="attempts"/> times and is not being worked on.</summary>
    public async Task<JsonObject> FailureAttemptsReachAsync(JsonObject library, string relativePath, int attempts, TimeSpan? timeout = null) =>
        await DriveRetriesUntilAsync<JsonObject>(
            library,
            async () =>
            {
                var row = await FileRowAsync(library, relativePath);
                return row is not null && Whole(row["failure_attempts"]) >= attempts && (string)row["status"]! != "processing" ? row : null;
            },
            $"{relativePath} to fail {attempts} times",
            timeout);

    /// <summary>
    /// Checks for <paramref name="duration"/> and fails as soon as <paramref name="happened"/> says yes. Watching can only show that
    /// something did not happen yet, so prefer asserting on state the server reports when it has any; use this for claims no API can answer.
    /// </summary>
    public static async Task NeverWithinAsync(Func<Task<bool>> happened, TimeSpan duration, string what)
    {
        var deadline = DateTime.UtcNow + duration;
        while (true)
        {
            Assert.False(await happened(), $"{what} happened within {duration.TotalSeconds:0}s, but it must not");
            if (DateTime.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    // --- plumbing -------------------------------------------------------------------------------

    /// <summary>A JSON number (or null) as a whole number; null counts as zero.</summary>
    public static long Whole(JsonNode? number) => number is null ? 0 : number.GetValue<long>();

    private async Task<List<JsonObject>> FilesAsync(JsonObject library)
    {
        var listing = await Admin.GetAsync($"{Api}/processing/files", ("limit", 1000), ("library_id", (int)library["id"]!));
        Assert.True(listing.Status == HttpStatusCode.OK, listing.ToString());
        return [.. listing.Fields["files"]!.AsArray().OfType<JsonObject>()];
    }

    private async Task<bool> ScanPendingAsync() => (await JobsAsync()).Any(job =>
        (string)job["status"]! is "pending" or "leased" && ((string)job["job_kind"]!).StartsWith("processing.watched_folder", StringComparison.Ordinal));

    private static IEnumerable<JsonObject> JobRows(WeirResponse inspection) => inspection.Fields["jobs"]!.AsArray().OfType<JsonObject>();
}
