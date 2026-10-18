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

    /// <summary>
    /// The reports for one hand-off once there are <paramref name="count"/>. A pass reports before its job ends, and a hand-off settled
    /// on arrival (a repeat of a cleaned file) is answered inside the request that brought it, so this waits for as long as a worker has
    /// a job (see <see cref="WaitWhileWorkedOnAsync{T}"/>) and fails as soon as every job has ended without the report.
    /// </summary>
    public async Task<List<JsonObject>> WaitForReportsAsync(FakeManager fake, string handoffId, int count = 1) =>
        await WaitWhileWorkedOnAsync(
            () =>
            {
                var reports = Callbacks(fake, handoffId);
                return Task.FromResult(reports.Count >= count ? reports : null);
            },
            $"{count} report(s) for {handoffId}");

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

    /// <summary>
    /// Returns once no job is pending or held by a worker (see <see cref="JobWait"/>). A file's status is written before the job that wrote
    /// it ends, and a job a worker still holds when the server stops is queued again when the server starts.
    /// </summary>
    public async Task WaitForNoJobInFlightAsync() =>
        await new JobWait().UntilAsync(
            JobStatusesAsync,
            async () => (await JobStatusesAsync()).Any(status => status is "pending" or "leased") ? null : "idle",
            "every job to end");

    /// <summary>Returns once no worker holds a job, whatever else is waiting for its turn (see <see cref="WaitForNoJobInFlightAsync"/>).</summary>
    public async Task WaitForNoJobHeldAsync() =>
        await new JobWait().UntilAsync(
            JobStatusesAsync,
            async () => (await JobStatusesAsync()).Contains("leased") ? null : "idle",
            "every job a worker holds to end");

    /// <summary>The job's inspection row once it has left <c>pending</c> and <c>leased</c>, for as long as a worker has it (see <see cref="JobWait"/>).</summary>
    public async Task<JsonObject> WaitForJobFinishedAsync(int jobId) =>
        await new JobWait().UntilAsync(
            async () => [.. (await JobsAsync()).Where(row => (int)row["id"]! == jobId).Select(row => (string)row["status"]!)],
            async () => (await FinishedJobsAsync()).FirstOrDefault(row => (int)row["id"]! == jobId),
            $"job {jobId} to finish");

    /// <summary>The jobs that have ended, of one kind or all. The statuses are named because the default inspection list leaves out completed scan jobs.</summary>
    public async Task<List<JsonObject>> FinishedJobsAsync(string? kind = null)
    {
        var inspection = await Admin.GetAsync(
            $"{Api}/processing/jobs/inspection", [("limit", 100), .. FinishedJobStatuses.Select(status => ("status", (object)status))]);
        Assert.True(inspection.Status == HttpStatusCode.OK, inspection.ToString());
        return JobRows(inspection).Where(row => kind is null || (string)row["job_kind"]! == kind).ToList();
    }

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
        await using var database = await StopForDatabaseAsync();
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
        await using (var database = await StopForDatabaseAsync())
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

    // --- showing that something did not happen ---------------------------------------------------

    /// <summary>The id of the newest job, whatever its status: the mark a later wait is measured from, as ids only go up.</summary>
    public async Task<int> LatestJobIdAsync() =>
        (await JobsAsync()).Concat(await FinishedJobsAsync()).Select(row => (int)row["id"]!).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Waits for a scan of <paramref name="library"/> that was queued after job <paramref name="mark"/> to finish: the periodic scan, or the
    /// folder watcher's. A claim that something did not happen while Weir was looking is only worth making after a look that began once
    /// the claim was in force.
    /// </summary>
    public async Task WaitForScanAfterAsync(JsonObject library, int mark) =>
        await WaitWhileWorkedOnAsync(
            async () => (await FinishedJobsAsync(ScanKind)).FirstOrDefault(row => (int)row["id"]! > mark && ScansLibrary(row, (int)library["id"]!)),
            $"a scan of {(string)library["name"]!} queued after job {mark} to finish",
            owed: () => Task.FromResult(true));

    /// <summary>Every scan of <paramref name="library"/> Weir has queued, running or ended. Other workflows' scans share the job kind and come and go on their own timers.</summary>
    public async Task<List<JsonObject>> ScansOfAsync(JsonObject library) =>
        [.. (await JobsAsync(ScanKind)).Concat(await FinishedJobsAsync(ScanKind)).Where(row => ScansLibrary(row, (int)library["id"]!))];

    private static bool ScansLibrary(JsonObject job, int libraryId) =>
        JsonNode.Parse((string)job["payload_json"]!) is JsonObject payload && payload["library_id"] is { } id && (int)id == libraryId;

    /// <summary>
    /// Waits until Weir has gone round its periodic scans once more, from a workflow of its own that nothing else in the scenario knows:
    /// a new file is put in its folder, and Weir has gone round when it has found it. A claim that Weir did not do something it would
    /// have done by now, if it did it, is made only after this: the round began after the claim was in force, so anything the claim
    /// forbids that a round would have started has been started by the time this returns. The file is under the workflow's minimum size,
    /// so the round is all it brings.
    /// </summary>
    public async Task WaitForARoundAsync()
    {
        if (_sentinel is null)
        {
            var folders = Folders.Make(Path.Combine(Root, "sentinel"));
            _sentinel = await CreateLibraryAsync(
                ("name", "Round Sentinel"),
                ("watched_folder", folders.Watched),
                ("work_folder", folders.Work),
                ("output_folder", folders.Output),
                ("scan_interval_seconds", 10),
                ("min_file_size_mb", 1000));
        }

        var relativePath = $"Round.{++_rounds}.2024/film.mkv";
        var path = Path.Combine((string)_sentinel["watched_folder"]!, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[16]);
        await Poll.UntilAsync(async () => await FileRowAsync(_sentinel, relativePath), $"Weir to go round its scans and find {relativePath}");
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
