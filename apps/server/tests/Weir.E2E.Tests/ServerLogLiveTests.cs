using System.Net;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>System › Logs › Server follows what the server writes: a new line appears in the open log without a reload.</summary>
public sealed class ServerLogLiveTests(E2EServer server) : E2ETestBase(server)
{
    // The log is read again at most every 3 s after a line arrives (use-log-entries.ts), so this leaves room for several reads.
    private const float LineArrivesMs = 20_000;

    [E2EFact]
    public async Task A_warning_the_server_writes_appears_in_the_open_Server_log_without_a_reload()
    {
        var page = await NewPageAsync();
        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenLogsAsync(page, "Server");
        var refusals = page.GetByTestId("log-row").Filter(new() { HasText = "auth event: login failed" });
        var before = await refusals.CountAsync();
        await page.EvaluateAsync("() => { window.__weirNotReloaded = true; }");

        // A wrong password is one thing the server writes a warning for.
        using var stranger = new WeirClient(new Uri(BaseUrl));
        var refused = await stranger.AttemptLoginAsync(Navigation.BootstrapUser, "not-the-password");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);

        await Expect(refusals).ToHaveCountAsync(before + 1, new() { Timeout = LineArrivesMs });
        Assert.True(await page.EvaluateAsync<bool>("() => window.__weirNotReloaded === true"), "The page was reloaded.");
    }
}
