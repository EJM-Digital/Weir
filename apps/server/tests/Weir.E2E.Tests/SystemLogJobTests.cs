using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Logs › Jobs: a job's row opens to what it is, one row at a time, and a queued job can be taken back. A rig of one worker over a fake ffmpeg keeps one
/// job running and the next one queued behind it until the test releases them, so the states are the real ones.
/// </summary>
public sealed class SystemLogJobTests(E2EServer server) : E2ETestBase(server)
{
    private const string Running = "Harbour Lights 2024.mkv";
    private const string Queued = "Night Train 2019.mkv";
    private const string TooLate = "This job is no longer waiting, so it can't be cancelled; the log shows where it is now.";
    private static readonly string[] JobFacts = ["Job", "Status", "Attempts", "Last changed", "Kind", "Dedupe key"];

    private static ILocator RowOf(IPage page, string text) => page.GetByTestId("log-row").Filter(new() { HasText = text });

    private static ILocator Head(ILocator row) => row.Locator("button.mm-log-row__head");

    private static ILocator FactsOf(ILocator row) => row.Locator("dl.mm-log-facts dt");

    private static ILocator Fact(ILocator row, string label) => row.Locator($"dl.mm-log-facts div:has(> dt:text-is('{label}')) > dd");

    private static async Task<long> JobIdAsync(ProcessingRig rig, string handoffId, string status)
    {
        var job = await Poll.UntilAsync(
            async () =>
            {
                var inspected = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/jobs/inspection", ("limit", 100));
                Assert.True(inspected.Status == HttpStatusCode.OK, inspected.ToString());
                return inspected.Fields["jobs"]!.AsArray()
                    .Select(job => job!.AsObject())
                    .FirstOrDefault(job => (string?)job["dedupe_key"] == $"processing.file.remux_pass.v1:deluno:handoff:{handoffId}" && (string?)job["status"] == status);
            },
            $"the job of {handoffId} to be {status}");
        return (long)job["id"]!;
    }

    /// <summary>A rig with one job running and the next one queued behind it, and a page open on Logs › Jobs.</summary>
    private async Task<(ProcessingRig Rig, IPage Page, long QueuedId)> OpenJobsAsync()
    {
        var rig = await ProcessingRig.StartAsync();
        try
        {
            await rig.HandOffAsync("run-1", Running);
            await rig.WaitUntilWorkingAsync("run-1");
            await rig.HandOffAsync("wait-1", Queued);
            var queuedId = await JobIdAsync(rig, "wait-1", "pending");
            var page = await NewPageAsync();
            await page.RunAndWaitForResponseAsync(
                () => Navigation.EnsureSignedInAsync(page, rig.BaseUrl),
                response => response.Url.EndsWith("/api/v1/activity/stream", StringComparison.Ordinal));
            await Navigation.OpenLogsAsync(page, "Jobs");
            await Expect(RowOf(page, Queued)).ToBeVisibleAsync();
            return (rig, page, queuedId);
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    [E2EFact]
    public async Task Rows_open_and_close_one_at_a_time_and_each_job_says_what_it_is_and_whether_a_worker_has_it()
    {
        var (rig, page, queuedId) = await OpenJobsAsync();
        await using var _rig = rig;
        var running = RowOf(page, Running);
        var queued = RowOf(page, Queued);

        await Head(running).ClickAsync();
        await Expect(Head(running)).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(FactsOf(running)).ToHaveTextAsync([.. JobFacts, "Worker lease"]);
        await Expect(Fact(running, "Status")).ToHaveTextAsync("Running");
        await Expect(Fact(running, "Worker lease")).Not.ToBeEmptyAsync();
        await Expect(Fact(running, "Kind")).ToHaveTextAsync("processing.file.remux_pass.v1");

        await Head(queued).ClickAsync();
        await Expect(Head(queued)).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(Head(running)).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(FactsOf(queued)).ToHaveTextAsync(JobFacts);
        await Expect(Fact(queued, "Job")).ToHaveTextAsync($"#{queuedId}");
        await Expect(Fact(queued, "Status")).ToHaveTextAsync("Queued");
        await Expect(Fact(queued, "Attempts")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^\d+ of \d+$"));
        await Expect(queued).ToContainTextAsync("Next step:");
        await Expect(queued.GetByRole(AriaRole.Button, new() { Name = "Cancel pending", Exact = true })).ToBeVisibleAsync();
        await Expect(queued.GetByRole(AriaRole.Button, new() { Name = "Recover result", Exact = true })).ToHaveCountAsync(0);

        await Head(queued).ClickAsync();
        await Expect(Head(queued)).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(page.Locator("button.mm-log-row__head[aria-expanded=true]")).ToHaveCountAsync(0);

        rig.ReleasePass(Running);
        rig.ReleasePass(Queued);
        await rig.WaitForWorkToEndAsync();
    }

    [E2EFact]
    public async Task While_Weir_is_paused_a_queued_job_says_it_is_waiting_for_the_resume_and_goes_back_to_its_own_words_after()
    {
        var (rig, page, _) = await OpenJobsAsync();
        await using var _rig = rig;
        var queued = RowOf(page, Queued);
        await Head(queued).ClickAsync();
        await Expect(queued).Not.ToContainTextAsync("Waiting · Weir is paused.");

        await WeirApi.SetPausedAsync(page.Context, rig.BaseUrl, paused: true);

        await Expect(queued).ToContainTextAsync("Waiting · Weir is paused.", new() { Timeout = 10_000 });
        await Expect(queued).ToContainTextAsync("Next step: Nothing to do. Resume at the top to continue.");

        await WeirApi.SetPausedAsync(page.Context, rig.BaseUrl, paused: false);

        await Expect(queued).Not.ToContainTextAsync("Waiting · Weir is paused.", new() { Timeout = 10_000 });
        await Expect(queued).Not.ToContainTextAsync("Resume at the top");
        rig.ReleasePass(Running);
        rig.ReleasePass(Queued);
        await rig.WaitForWorkToEndAsync();
    }

    [E2EFact]
    public async Task Cancel_pending_takes_a_queued_job_back_and_the_log_shows_it_cancelled()
    {
        var (rig, page, queuedId) = await OpenJobsAsync();
        await using var _rig = rig;
        var (requests, reads) = (RequestLog.Watch(page), ReadsSent.Watch(page));
        var queued = RowOf(page, Queued);
        await Head(queued).ClickAsync();
        var mark = requests.Mark();
        var readMark = reads.Mark();

        await queued.GetByRole(AriaRole.Button, new() { Name = "Cancel pending", Exact = true }).ClickAsync();

        await Expect(Fact(queued, "Status")).ToHaveTextAsync("Cancelled");
        await Expect(queued.GetByRole(AriaRole.Button, new() { Name = "Cancel pending", Exact = true })).ToHaveCountAsync(0);
        var sent = Assert.Single(requests.Since(mark));
        Assert.Equal(("POST", $"/api/v1/processing/jobs/{queuedId}/cancel-pending"), (sent.Method, sent.PathAndQuery));
        Assert.Equal(["csrf_token"], JsonNode.Parse(sent.Body!)!.AsObject().Select(field => field.Key));
        Assert.NotEmpty(reads.Since(readMark, "/api/v1/system/log"));
        var inspected = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/jobs/inspection", ("limit", 100));
        Assert.Equal("cancelled", (string?)inspected.Fields["jobs"]!.AsArray().Single(job => (long)job!["id"]! == queuedId)!["status"]);

        rig.ReleasePass(Running);
        await rig.WaitForWorkToEndAsync();
    }

    [E2EFact]
    public async Task A_cancel_that_comes_too_late_says_in_plain_words_that_the_job_is_no_longer_waiting()
    {
        var (rig, page, queuedId) = await OpenJobsAsync();
        await using var _rig = rig;
        var queued = RowOf(page, Queued);
        await Head(queued).ClickAsync();
        await Expect(Fact(queued, "Status")).ToHaveTextAsync("Queued");

        // The one worker finishes the first job and takes this one while the row is open: the list holds still for a row being read.
        rig.ReleasePass(Running);
        await JobIdAsync(rig, "wait-1", "leased");
        await queued.GetByRole(AriaRole.Button, new() { Name = "Cancel pending", Exact = true }).ClickAsync();

        await Expect(queued.GetByRole(AriaRole.Alert)).ToHaveTextAsync(TooLate);
        await Expect(queued.GetByRole(AriaRole.Button, new() { Name = "Cancel pending", Exact = true })).ToBeEnabledAsync();
        var inspected = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/jobs/inspection", ("limit", 100));
        Assert.NotEqual("cancelled", (string?)inspected.Fields["jobs"]!.AsArray().Single(job => (long)job!["id"]! == queuedId)!["status"]);

        rig.ReleasePass(Queued);
        await rig.WaitForWorkToEndAsync();
    }
}
