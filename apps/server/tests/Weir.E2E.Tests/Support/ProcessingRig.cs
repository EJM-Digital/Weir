using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// A Weir of its own that really processes files, for a test that watches a file move through the screens. It runs one worker
/// against a fake ffmpeg, so a pass stays under way until the test releases it, and a fake Deluno takes the finished
/// hand-off's report. Files arrive and finish the way a media manager makes them: a hand-off on the webhook, then the
/// manager's word on what it did with the copy. Nothing is written to the database.
/// </summary>
public sealed class ProcessingRig : IAsyncDisposable
{
    private const string WebhookSecret = "e2e-rig-webhook-secret";
    private const string WebhookSecretVariable = "WEIR_MEDIA_MANAGER_WEBHOOK_SECRET";
    private const int InspectedJobs = 100;
    private const string CallbackPath = "/api/integrations/processors/events";

    // A pass is a few processes to start, which a busy machine stretches; this is where a wait gives up, not how long one takes.
    private static readonly TimeSpan PassCeiling = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyDictionary<string, string> Secret = new Dictionary<string, string> { ["X-Webhook-Secret"] = WebhookSecret };

    private readonly FakeFfmpeg _tools;
    private readonly WeirServer _server;
    private readonly WeirClient _admin;
    private readonly FakeManager _deluno;
    private readonly TemporaryFolder _folders;

    private ProcessingRig(FakeFfmpeg tools, WeirServer server, WeirClient admin, FakeManager deluno, TemporaryFolder folders)
    {
        _tools = tools;
        _server = server;
        _admin = admin;
        _deluno = deluno;
        _folders = folders;
    }

    /// <summary>The server's address without a trailing slash.</summary>
    public string BaseUrl => _server.BaseUrl.GetLeftPart(UriPartial.Authority);

    /// <summary>The rig's admin session, for reading and arranging what the screens show.</summary>
    public WeirClient Admin => _admin;

    private string Watched => Path.Combine(_folders.Path, "watched");

    /// <param name="minimumFileSizeMb">The Movies workflow's minimum size: a file smaller than this is left alone.</param>
    /// <param name="holdFailures">A file that fails stays failed, for the person to try again, instead of being retried by itself.</param>
    public static async Task<ProcessingRig> StartAsync(int minimumFileSizeMb = 0, bool holdFailures = false)
    {
        var tools = FakeFfmpeg.Install();
        var folders = new TemporaryFolder();
        var deluno = FakeManager.StartDeluno();
        WeirServer? server = null;
        WeirClient? admin = null;
        try
        {
            server = await WeirServer.StartNewAsync(new Dictionary<string, string>(tools.Env)
            {
                ["WEIR_WEB_DIST"] = RepoPaths.WebDist,
                ["WEIR_PROCESSING_WORKER_COUNT"] = "1",
                [WebhookSecretVariable] = WebhookSecret,
            });
            admin = new WeirClient(server.BaseUrl);
            await admin.EnsureAdminAsync(Navigation.BootstrapUser, Navigation.BootstrapPassword);
            await PrepareMoviesWorkflowAsync(admin, folders.Path, minimumFileSizeMb, holdFailures);
            var connection = await admin.PostWithCsrfAsync($"{WeirClient.Api}/media-managers/connections", new JsonObject
            {
                ["kind"] = "deluno",
                ["base_url"] = deluno.BaseUrl,
                ["api_key"] = deluno.ApiKey,
                ["enabled"] = true,
            });
            Assert.True(connection.Status == HttpStatusCode.Created, connection.ToString());
            return new ProcessingRig(tools, server, admin, deluno, folders);
        }
        catch
        {
            admin?.Dispose();
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            deluno.Dispose();
            tools.Dispose();
            folders.Dispose();
            throw;
        }
    }

    /// <summary>Points the seeded Movies workflow at folders that exist, with no wait, and leaves originals where they are, as a manager-fed workflow does.</summary>
    private static async Task PrepareMoviesWorkflowAsync(WeirClient admin, string root, int minimumFileSizeMb, bool holdFailures)
    {
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        var watched = Directory.CreateDirectory(Path.Combine(root, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var libraries = $"{WeirClient.Api}/processing/libraries";
        var listed = await admin.GetAsync(libraries);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        var movies = listed.Elements.Select(row => row!.AsObject()).First(row => (string?)row["media_type"] == "movie");
        var body = LibraryBodies.Unchanged(movies);
        body["watched_folder"] = watched;
        body["output_folder"] = output;
        body["work_folder"] = work;
        body["ready_after_seconds"] = 0;
        body["skip_access_tests"] = true;
        body["min_file_size_mb"] = minimumFileSizeMb;
        body["remove_original_after_success"] = false;
        if (holdFailures)
        {
            body["failure_policy"] = "hold";
            body["max_attempts"] = 1;
        }

        var saved = await admin.PutWithCsrfAsync($"{libraries}/{(long)movies["id"]!}", body);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        var settings = await admin.PutWithCsrfAsync($"{WeirClient.Api}/processing/operator-settings", new JsonObject { ["minimum_free_disk_space_mb"] = 0 });
        Assert.True(settings.Status == HttpStatusCode.OK, settings.ToString());
    }

    /// <summary>
    /// Deluno hands over a movie. Its pass stays under way, once a worker takes it, until <see cref="ReleasePass"/>. The hand-off
    /// is answered by the server before the file moves anywhere on screen.
    /// </summary>
    public async Task HandOffAsync(string handoffId, string fileName)
    {
        var source = Path.Combine(Watched, handoffId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, FakeMedia.Bytes(FakeMedia.Probe()));
        _tools.SetFileRule(fileName, new FileRule { Probe = FakeMedia.Probe(), RemuxReleaseFile = ReleaseFileFor(fileName) });
        await PostHandOffAsync(handoffId, source);
    }

    /// <summary>
    /// Deluno hands over a single file that sits directly in the watched folder, with no release folder around it, as a
    /// download client leaves one. Its pass stays under way, once a worker takes it, until <see cref="ReleasePass"/>.
    /// </summary>
    public async Task HandOffRootFileAsync(string handoffId, string fileName)
    {
        var source = Path.Combine(Watched, fileName);
        await File.WriteAllBytesAsync(source, FakeMedia.Bytes(FakeMedia.Probe()));
        _tools.SetFileRule(fileName, new FileRule { Probe = FakeMedia.Probe(), RemuxReleaseFile = ReleaseFileFor(fileName) });
        await PostHandOffAsync(handoffId, source);
    }

    /// <summary>Deluno hands over the file it already handed over, which has not changed since: no file is written.</summary>
    public Task HandOffRootFileAgainAsync(string handoffId, string fileName) => PostHandOffAsync(handoffId, Path.Combine(Watched, fileName));

    /// <summary>The file's row as the server lists it for Activity, read back through the API, or null when Activity does not list it.</summary>
    public async Task<JsonObject?> ListedFileAsync(string fileName)
    {
        var listed = await _admin.GetAsync($"{WeirClient.Api}/processing/files", ("path_contains", fileName));
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return listed.Fields["files"]!.AsArray().SingleOrDefault()?.AsObject();
    }

    /// <summary>Whether the file Deluno handed over is still where it was put, in the watched folder.</summary>
    public bool WatchedFileExists(string handoffId, string fileName) => File.Exists(Path.Combine(Watched, handoffId, fileName));

    /// <summary>Takes the file off Activity's list, as "Remove from the list" does.</summary>
    public async Task ForgetAsync(string fileName)
    {
        var listed = await _admin.GetAsync($"{WeirClient.Api}/processing/files", ("path_contains", fileName));
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        var id = (long)listed.Fields["files"]!.AsArray().Single()!["id"]!;
        var forgotten = await _admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/processing/files/{id}");
        Assert.True(forgotten.Status == HttpStatusCode.NoContent, forgotten.ToString());
    }

    /// <summary>
    /// Deluno hands over a release folder: the film, and an extra too small for the workflow's minimum size. The film's pass stays
    /// under way until <see cref="ReleasePass"/>, or, when <paramref name="failFirstPass"/> is set, fails the first time and
    /// succeeds at once after that.
    /// </summary>
    public async Task HandOffReleaseAsync(string handoffId, string film, string extra, bool failFirstPass = false)
    {
        var folder = Path.Combine(Watched, handoffId);
        Directory.CreateDirectory(folder);
        // The film is padded just past a megabyte so it clears the workflow's minimum, and the extra is not.
        await File.WriteAllBytesAsync(Path.Combine(folder, film), FakeMedia.Bytes(FakeMedia.Probe(), padding: 1_100_000));
        await File.WriteAllBytesAsync(Path.Combine(folder, extra), FakeMedia.Bytes(FakeMedia.Probe()));
        _tools.SetFileRule(
            film,
            failFirstPass
                ? new FileRule { Probe = FakeMedia.Probe(), RemuxError = "Conversion failed: the fake ffmpeg was told to fail", RemuxFailTimes = 1 }
                : new FileRule { Probe = FakeMedia.Probe(), RemuxReleaseFile = ReleaseFileFor(film) });
        await PostHandOffAsync(handoffId, folder);
    }

    private async Task PostHandOffAsync(string handoffId, string sourcePath)
    {
        using var deluno = _server.CreateClient();
        var response = await deluno.PostAsync($"{WeirClient.Api}/intake/webhook/deluno", new JsonObject
        {
            ["eventType"] = "deluno.processor-handoff",
            ["handoffId"] = handoffId,
            ["libraryId"] = "lib-1",
            ["mediaType"] = "movies",
            ["sourcePath"] = sourcePath,
            ["callbackPath"] = CallbackPath,
        }, Secret);
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
    }

    /// <summary>How many times ffmpeg has written a cleaned copy since the rig started.</summary>
    public int RemuxCount => _tools.Calls(tool: "ffmpeg", step: "remux").Count;

    /// <summary>Waits until a worker has taken the hand-off's pass, which then stays under way until <see cref="ReleasePass"/>.</summary>
    public async Task WaitUntilWorkingAsync(string handoffId) =>
        await Poll.UntilAsync(
            async () =>
            {
                var inspected = await _admin.GetAsync($"{WeirClient.Api}/processing/jobs/inspection", ("limit", InspectedJobs));
                Assert.True(inspected.Status == HttpStatusCode.OK, inspected.ToString());
                return inspected.Fields["jobs"]!.AsArray().Any(job =>
                    (string?)job!["dedupe_key"] == $"processing.file.remux_pass.v1:deluno:handoff:{handoffId}" && (string?)job["status"] == "leased");
            },
            $"Weir to start working {handoffId}",
            PassCeiling);

    /// <summary>Deluno stops answering, so a report Weir has for it cannot be delivered and Weir keeps it.</summary>
    public void StopDeluno() => _deluno.Dispose();

    /// <summary>Waits until Weir holds a report it owes Deluno because Deluno did not answer.</summary>
    public async Task WaitForOwedReportAsync() =>
        await Poll.UntilAsync(
            () => Task.FromResult(OwedReports > 0),
            "Weir to keep the report it could not deliver",
            PassCeiling);

    /// <summary>How many finished hand-offs Weir still owes Deluno a report for.</summary>
    public long OwedReports => E2EDatabase.OwedReportCount(_server.DatabasePath);

    /// <summary>Lets the pass on <paramref name="fileName"/> finish.</summary>
    public void ReleasePass(string fileName) => File.WriteAllText(ReleaseFileFor(fileName), string.Empty);

    /// <summary>
    /// Waits until ffmpeg has started <paramref name="count"/> writes in all, for as long as a worker has a job. A pass spawns its tools
    /// one after another, and how long each takes to start depends on how busy the machine is (see <see cref="JobWait"/>).
    /// </summary>
    public async Task WaitForWritesStartedAsync(int count) =>
        await new JobWait().UntilAsync(
            JobStatusesAsync,
            () => Task.FromResult(RemuxCount >= count ? "writing" : null),
            $"ffmpeg to start write {count}");

    /// <summary>Waits until no job is waiting or being worked on, for as long as a worker has one (see <see cref="JobWait"/>).</summary>
    public async Task WaitForWorkToEndAsync() =>
        await new JobWait().UntilAsync(
            JobStatusesAsync,
            async () => (await JobStatusesAsync()).All(status => status is not ("pending" or "leased")) ? "ended" : null,
            "Weir to finish its work");

    private async Task<IReadOnlyList<string>> JobStatusesAsync()
    {
        var inspection = await _admin.GetAsync($"{WeirClient.Api}/processing/jobs/inspection", ("limit", 100));
        Assert.True(inspection.Status == HttpStatusCode.OK, inspection.ToString());
        return [.. inspection.Fields["jobs"]!.AsArray().Select(job => (string)job!["status"]!)];
    }

    /// <summary>Waits until Weir has handed the finished file back to Deluno and told it so.</summary>
    public async Task WaitForReportAsync() =>
        await _deluno.WaitForRequestAsync("POST", CallbackPath);

    /// <summary>Deluno says what it did with the copy Weir handed back, as it does once it has imported it.</summary>
    public async Task ReportImportedAsync(string handoffId)
    {
        using var deluno = _server.CreateClient();
        var response = await deluno.PostAsync(
            $"{WeirClient.Api}/intake/handoffs/deluno/{handoffId}/outcome",
            new JsonObject { ["outcome"] = "imported", ["occurredUtc"] = DateTimeOffset.UtcNow.ToString("O") },
            Secret);
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
    }

    private string ReleaseFileFor(string fileName) => Path.Combine(_folders.Path, $"release-{fileName}");

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _server.DisposeAsync();
        _deluno.Dispose();
        _tools.Dispose();
        _folders.Dispose();
    }
}
