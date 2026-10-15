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

    /// <summary>
    /// Waits for the hand-off to reach <paramref name="state"/>, for as long as a worker has its job: how long a pass takes depends on the
    /// machine (see <see cref="JobWait"/>).
    /// </summary>
    public async Task<JsonObject> WaitForHandoffStateAsync(string handoffId, string state) =>
        await new JobWait().UntilAsync(
            JobStatusesAsync,
            async () =>
            {
                var status = await HandoffStatusAsync(handoffId);
                return (string)status["state"]! == state ? status : null;
            },
            $"hand-off {handoffId} to reach '{state}'");

    /// <summary>The completion and failure reports the fake manager received for one hand-off.</summary>
    public static List<JsonObject> Callbacks(FakeManager fake, string handoffId) => fake.RequestsTo("POST", EventsPath)
        .Select(request => request.Json as JsonObject)
        .OfType<JsonObject>()
        .Where(report => (string?)report["handoffId"] == handoffId)
        .ToList();

    // --- jobs -----------------------------------------------------------------------------------

    /// <summary>
    /// The status of every job, from a session on the server's current address: stopping the server for its database starts it again on a
    /// new port, which leaves <see cref="Admin"/> signed in on the old one.
    /// </summary>
    public async Task<IReadOnlyList<string>> JobStatusesAsync()
    {
        if (Admin.BaseAddress != Server.BaseUrl)
        {
            Admin.Dispose();
            Admin = Server.CreateClient();
            await Admin.LoginAsync();
        }

        return [.. (await JobsAsync()).Select(job => (string)job["status"]!)];
    }

    /// <summary>
    /// Waits for <paramref name="result"/> for as long as a worker holds any of Weir's jobs (see <see cref="JobWait"/>). Every job is
    /// watched, not only the one that starts the work, so a job it queues before it ends (a reject, a pass-through) keeps the wait going.
    /// <paramref name="owed"/> says when Weir owes work it queues on its own timer (a periodic scan, a retry once its backoff has passed):
    /// that counts as a pending job, so the wait gives up only when nothing takes it up within <see cref="JobWait.UnclaimedCeiling"/>.
    /// </summary>
    public async Task<T> WaitWhileWorkedOnAsync<T>(Func<Task<T?>> result, string what, Func<Task<bool>>? owed = null)
        where T : class =>
        await new JobWait().UntilAsync(
            async () => owed is not null && await owed() ? [.. await JobStatusesAsync(), "pending"] : await JobStatusesAsync(),
            result,
            what);

    public async Task WaitWhileWorkedOnAsync(Func<Task<bool>> condition, string what, Func<Task<bool>>? owed = null) =>
        await WaitWhileWorkedOnAsync(async () => await condition() ? "done" : null, what, owed);

    public async Task<List<JsonObject>> JobsAsync(string? kind = null)
    {
        var inspection = await Admin.GetAsync($"{Api}/processing/jobs/inspection", ("limit", 100));
        Assert.True(inspection.Status == HttpStatusCode.OK, inspection.ToString());
        return JobRows(inspection).Where(row => kind is null || (string)row["job_kind"]! == kind).ToList();
    }

    /// <summary>The job's inspection row once it has left <c>pending</c> and <c>leased</c>, for as long as a worker has it (see <see cref="JobWait"/>).</summary>
    public async Task<JsonObject> WaitForJobFinishedAsync(int jobId) =>
        await new JobWait().UntilAsync(
            async () => [.. (await JobsAsync()).Where(row => (int)row["id"]! == jobId).Select(row => (string)row["status"]!)],
            async () =>
            {
                // Named explicitly: the default inspection list leaves out completed scan jobs.
                var inspection = await Admin.GetAsync(
                    $"{Api}/processing/jobs/inspection", [("limit", 100), .. FinishedJobStatuses.Select(status => ("status", (object)status))]);
                Assert.True(inspection.Status == HttpStatusCode.OK, inspection.ToString());
                return JobRows(inspection).FirstOrDefault(row => (int)row["id"]! == jobId);
            },
            $"job {jobId} to finish");

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

    /// <summary>
    /// Waits for the file to reach <paramref name="status"/>, for as long as a worker has a job: how long a pass takes depends on the
    /// machine (see <see cref="JobWait"/>).
    /// </summary>
    public async Task<JsonObject> WaitForFileStatusAsync(JsonObject library, string relativePath, string status) =>
        await new JobWait().UntilAsync(
            JobStatusesAsync,
            async () =>
            {
                var row = await FileRowAsync(library, relativePath);
                return row is not null && (string)row["status"]! == status ? row : null;
            },
            $"{relativePath} to reach status '{status}'");

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
    /// hand-off queues its own retry, so there the scenario only waits. A file waiting for its retry is work Weir owes, so the wait lasts
    /// as long as there is a retry to come or a worker has a job (see <see cref="WaitWhileWorkedOnAsync{T}"/>).
    /// </summary>
    public async Task DriveRetriesUntilAsync(JsonObject library, Func<Task<bool>> probe, string what) =>
        await DriveRetriesUntilAsync(library, async () => await probe() ? "done" : null, what);

    public async Task<T> DriveRetriesUntilAsync<T>(JsonObject library, Func<Task<T?>> probe, string what)
        where T : class
    {
        var sinceLastScan = new Stopwatch();
        return await WaitWhileWorkedOnAsync(
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
            async () => (await FilesAsync(library)).Any(row => (string)row["status"]! == "processing_failed" && row["next_retry_at"] is not null));
    }

    /// <summary>Drives retries until the file has failed <paramref name="attempts"/> times and is not being worked on.</summary>
    public async Task<JsonObject> FailureAttemptsReachAsync(JsonObject library, string relativePath, int attempts) =>
        await DriveRetriesUntilAsync<JsonObject>(
            library,
            async () =>
            {
                var row = await FileRowAsync(library, relativePath);
                return row is not null && Whole(row["failure_attempts"]) >= attempts && (string)row["status"]! != "processing" ? row : null;
            },
            $"{relativePath} to fail {attempts} times");

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
