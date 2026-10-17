using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// "Try the failed files again" above the Failed list, in a real browser against a Weir that holds a file in each state: the button counts
/// the failed files shown (one file, two files), turns off while the server takes the request, asks the server for exactly those files and
/// shows what the server answered; when the server cannot queue them it says so and the files stay failed.
/// </summary>
public sealed partial class ActivityRetryFailedTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 20_000;
    private const float PassMs = 180_000;
    private static readonly ViewportSize Wide = new() { Width = 1900, Height = 1000 };

    private const string AnotherFailed = "Failed Again 2022.mkv";
    private const string ExtraOfAnotherFailed = "Extra E.mkv";

    [GeneratedRegex(@"[A-Za-z0-9 ]+\.mkv")]
    private static partial Regex FileName();

    [E2EFact]
    public async Task With_one_failed_file_the_button_says_so_turns_off_while_the_server_takes_it_and_shows_the_servers_answer()
    {
        await using var rig = await MixedActivity.StartAsync(writesGiveUpQuickly: false);
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log) = await OpenFailedAsync(rig);
            var retry = page.GetByRole(AriaRole.Button, new() { Name = "Try the 1 failed file again", Exact = true });
            await Expect(retry).ToBeVisibleAsync();
            var failedId = (long)(await rig.ListedFileAsync(MixedActivity.Failed))!["id"]!;
            var mark = log.Mark();
            var answered = page.WaitForResponseAsync(response => response.Url.EndsWith("/processing/files/requeue", StringComparison.Ordinal));

            using (rig.HoldWrites())
            {
                await retry.ClickAsync();

                await Expect(retry).ToBeDisabledAsync();
            }

            var response = await answered;
            Assert.Equal(200, response.Status);
            var detail = (string)JsonNode.Parse(await response.TextAsync())!["detail"]!;
            await Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = detail }).First).ToBeVisibleAsync(new() { Timeout = PushMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("POST", sent.Method);
            Assert.Equal([failedId], [.. JsonNode.Parse(sent.Body!)!["file_ids"]!.AsArray().Select(id => (long)id!)]);

            rig.ReleasePass(MixedActivity.Working);

            await Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Try the") })).ToHaveCountAsync(0, new() { Timeout = PassMs });
            Assert.Equal("processed", (string)(await rig.ListedFileAsync(MixedActivity.Failed))!["status"]!);
        });
    }

    [E2EFact]
    public async Task With_two_failed_files_the_button_names_both_acts_on_exactly_those_and_says_so_when_the_server_cannot_queue_them()
    {
        await using var rig = await MixedActivity.StartAsync(writesGiveUpQuickly: true);
        await rig.WithEvidenceAsync(async () =>
        {
            await rig.HandOffReleaseAsync("h-failed-again", AnotherFailed, ExtraOfAnotherFailed, failFirstPass: true);
            rig.ReleasePass(MixedActivity.Working);
            await Poll.UntilAsync(async () => (string?)(await rig.ListedFileAsync(AnotherFailed))?["status"] == "processing_failed", "the second film to fail", TimeSpan.FromMinutes(3));
            var (page, log) = await OpenFailedAsync(rig);
            var retry = page.GetByRole(AriaRole.Button, new() { Name = "Try the 2 failed files again", Exact = true });
            await Expect(retry).ToBeVisibleAsync(new() { Timeout = PushMs });
            await Expect(Rows(page)).ToHaveCountAsync(2);

            using (rig.HoldWrites())
            {
                var refused = await page.RunAndWaitForResponseAsync(() => retry.ClickAsync(), response => response.Url.EndsWith("/processing/files/requeue", StringComparison.Ordinal), new() { Timeout = PushMs });

                Assert.Equal(503, refused.Status);
                await Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Those files could not be queued again." }).First).ToBeVisibleAsync(new() { Timeout = PushMs });
            }

            await Expect(retry).ToBeEnabledAsync();
            Assert.Equal("processing_failed", (string)(await rig.ListedFileAsync(MixedActivity.Failed))!["status"]!);
            Assert.Equal("processing_failed", (string)(await rig.ListedFileAsync(AnotherFailed))!["status"]!);
            var ids = new[] { (long)(await rig.ListedFileAsync(MixedActivity.Failed))!["id"]!, (long)(await rig.ListedFileAsync(AnotherFailed))!["id"]! };
            var mark = log.Mark();

            await retry.ClickAsync();

            await Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Try the") })).ToHaveCountAsync(0, new() { Timeout = PassMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal(ids.Order(), JsonNode.Parse(sent.Body!)!["file_ids"]!.AsArray().Select(id => (long)id!).Order());
            Assert.Equal("processed", (string)(await rig.ListedFileAsync(AnotherFailed))!["status"]!);
        });
    }

    private async Task<(IPage Page, RequestLog Log)> OpenFailedAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync(Wide);
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/activity?show=failed");
        await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = new("^Failed") })).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = PushMs });
        await Expect(Rows(page).First).ToBeVisibleAsync(new() { Timeout = PushMs });
        return (page, log);
    }

    private static ILocator Rows(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = FileName() });
}
