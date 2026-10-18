using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Logs › Jobs › Recover result: a job that did its media work and could not record it is saved as finished from its row. No button can bring a
/// job to that state, so the row is written into the server's database as a worker that stopped at that point leaves it; every other step is the page's and the server's own.
/// </summary>
public sealed class SystemLogRecoverResultTests(E2EServer server) : E2ETestBase(server)
{
    private const string Kind = "processing.file.remux_pass.v1";
    private const string Stopped = "Weir could not record the result after the file was written.";
    private const string NotWaiting = "This job is not waiting for its result to be saved, so there is nothing to recover; the log shows where it is now.";

    private static ILocator Row(IPage page) => page.GetByTestId("log-row");

    private static ILocator Recover(IPage page) => Row(page).GetByRole(AriaRole.Button, new() { Name = "Recover result", Exact = true });

    private static ILocator Status(IPage page) => Row(page).Locator("dl.mm-log-facts div:has(> dt:text-is('Status')) > dd");

    private async Task<(SystemSession System, long JobId)> OpenRecoveryJobAsync()
    {
        var system = await SystemSession.OpenAsync(await NewPageAsync(), "About");
        var jobId = E2EDatabase.InsertJob(system.Server.DatabasePath, $"e2e.recover.{Guid.NewGuid():N}", Kind, "handler_ok_finalize_failed", Stopped);
        await system.Page.GotoAsync(
            $"{system.Server.BaseUrl.GetLeftPart(UriPartial.Authority)}/system?tab=logs&source=job&job={jobId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Row(system.Page).Locator("button.mm-log-row__head").ClickAsync();
        await Expect(Status(system.Page)).ToHaveTextAsync("Recovery needed");
        return (system, jobId);
    }

    [E2EFact]
    public async Task Recover_result_saves_the_job_as_finished_and_the_row_shows_it()
    {
        var (system, jobId) = await OpenRecoveryJobAsync();
        await using var _system = system;
        var (page, requests) = (system.Page, system.Requests);
        await Expect(Row(page)).ToContainTextAsync(Stopped);
        await PageTrail.WatchTextAsync(page, "Recovering…");
        var mark = requests.Mark();

        await Recover(page).ClickAsync();

        await Expect(Status(page)).ToHaveTextAsync("Finished");
        await Expect(Recover(page)).ToHaveCountAsync(0);
        Assert.True(await PageTrail.SawAsync(page, "Recovering…"), "The button never said it was recovering.");
        var sent = Assert.Single(requests.Since(mark));
        Assert.Equal(("POST", $"/api/v1/processing/jobs/{jobId}/recover-finalize-failed"), (sent.Method, sent.PathAndQuery));
        Assert.Equal(["csrf_token"], JsonNode.Parse(sent.Body!)!.AsObject().Select(field => field.Key));
        var log = await system.ReadAsync($"/api/v1/system/log?job={jobId}&source=job");
        Assert.Equal("completed", (string?)Assert.Single(log["items"]!.AsArray())!["job"]!["status"]);
    }

    [E2EFact]
    public async Task A_recovery_that_comes_too_late_says_in_plain_words_that_there_is_nothing_to_recover()
    {
        var (system, jobId) = await OpenRecoveryJobAsync();
        await using var _system = system;
        var page = system.Page;
        E2EDatabase.SetJobStatus(system.Server.DatabasePath, jobId, "completed");

        await Recover(page).ClickAsync();

        await Expect(Row(page).GetByRole(AriaRole.Alert)).ToHaveTextAsync(NotWaiting);
        await Expect(Recover(page)).ToBeEnabledAsync();
        await Expect(Recover(page)).ToHaveTextAsync("Recover result");
    }

    [E2EFact]
    public async Task A_recovery_the_server_cannot_do_says_so_and_the_job_stays_waiting_to_be_recovered()
    {
        var (system, _) = await OpenRecoveryJobAsync();
        await using var _system = system;
        var page = system.Page;
        await system.Server.StopAsync();

        await Recover(page).ClickAsync();

        await Expect(Row(page).GetByRole(AriaRole.Alert)).ToHaveTextAsync("Can't reach Weir. Check it's still running.", new() { Timeout = 20_000 });
        await Expect(Status(page)).ToHaveTextAsync("Recovery needed");
        await Expect(Recover(page)).ToBeEnabledAsync();
    }
}
