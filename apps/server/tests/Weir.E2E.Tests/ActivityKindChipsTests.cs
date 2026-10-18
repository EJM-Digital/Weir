using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Activity in a real browser against a Weir that holds a file in every state (one in progress, one finished, one on hold, three skipped, one
/// failed): the page opens on all of them, newest first, over the last 7 days, with the newest file open; each kind-of-file chip lists
/// exactly the files the server has in that state; and choosing another file opens it and writes it in the address.
/// </summary>
public sealed partial class ActivityKindChipsTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 20_000;
    private static readonly ViewportSize Wide = new() { Width = 1900, Height = 1000 };

    [GeneratedRegex(@"[A-Za-z0-9 ]+\.mkv")]
    private static partial Regex FileName();

    [E2EFact]
    public async Task Opening_Activity_with_no_choices_lists_everything_of_the_last_7_days_newest_first_with_the_newest_file_open()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await NewPageAsync(Wide);
            var log = RequestLog.Watch(page);
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);

            await page.GotoAsync($"{rig.BaseUrl}/activity");

            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
            await Expect(Chip(page, "All")).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "How far back" })).ToContainTextAsync("Last 7 days");
            await Expect(page).Not.ToHaveURLAsync(new Regex(@"[?&](show|within|file|library|q)="));
            var newestFirst = await ServerOrderAsync(rig, ("sort", "when"), ("direction", "desc"));
            Assert.Equal(newestFirst, await ListedAsync(page));
            await Expect(Rows(page).First).ToHaveAttributeAsync("aria-current", "true");
            await Expect(page.GetByTestId("activity-detail")).ToContainTextAsync(newestFirst[0]);
            // The list's own read is the one that asks for a sort: the badge in the menu reads files too, but by status and with no period.
            var asked = log.ReadsSince(0).Where(read => read.PathAndQuery.StartsWith("/api/v1/processing/files?", StringComparison.Ordinal) && read.PathAndQuery.Contains("sort=", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(asked);
            Assert.All(asked, read =>
            {
                Assert.Contains("within_days=7", read.PathAndQuery, StringComparison.Ordinal);
                Assert.Contains("sort=when", read.PathAndQuery, StringComparison.Ordinal);
                Assert.Contains("direction=desc", read.PathAndQuery, StringComparison.Ordinal);
            });
            Assert.Contains(log.ReadsSince(0), read => read.PathAndQuery.StartsWith("/api/v1/processing/library-cleans?", StringComparison.Ordinal) && read.PathAndQuery.Contains("within_days=7", StringComparison.Ordinal));
        });
    }

    [E2EFact]
    public async Task Each_kind_of_file_chip_lists_exactly_the_files_the_server_has_in_that_state_and_puts_the_choice_in_the_address()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenActivityAsync(rig);
            var chips = new (string Chip, string Show, string[] Files, string Status)[]
            {
                ("In progress", "working", [MixedActivity.Working], "processing"),
                ("Finished", "finished", [MixedActivity.Finished], "processed"),
                ("On hold", "needs", [MixedActivity.Held], "on_hold"),
                ("Skipped", "skipped", [MixedActivity.ExtraOfFailed, MixedActivity.ExtraOfFinished, MixedActivity.ExtraOfWorking], "skipped"),
            };

            foreach (var (chip, show, files, status) in chips)
            {
                await Chip(page, chip).ClickAsync();

                await Expect(Chip(page, chip)).ToHaveAttributeAsync("aria-pressed", "true");
                await Expect(page).ToHaveURLAsync(new Regex($"[?&]show={show}(&|$)"), new() { Timeout = PushMs });
                await Expect(Rows(page)).ToHaveCountAsync(files.Length, new() { Timeout = PushMs });
                Assert.Equal(files.Order(), (await ListedAsync(page)).Order());
                await Expect(Chip(page, chip)).ToContainTextAsync(files.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var file in files)
                {
                    Assert.Equal(status, (string)(await rig.ListedFileAsync(file))!["status"]!);
                }
            }

            await Chip(page, "All").ClickAsync();

            await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
            await Expect(page).Not.ToHaveURLAsync(new Regex(@"[?&]show="));
        });
    }

    [E2EFact]
    public async Task Choosing_another_file_opens_it_and_writes_it_in_the_address_in_place_of_the_one_before()
    {
        await using var rig = await MixedActivity.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenActivityAsync(rig);
            var ordered = await ServerFilesAsync(rig);
            var (firstName, secondName, thirdName) = (ordered[0].Name, ordered[1].Name, ordered[2].Name);
            await Expect(Rows(page).First).ToHaveAttributeAsync("aria-current", "true");
            await Expect(page.GetByTestId("activity-detail")).ToContainTextAsync(firstName);

            await Rows(page).Nth(1).GetByRole(AriaRole.Button).ClickAsync();

            await Expect(page).ToHaveURLAsync(new Regex($"[?&]file={ordered[1].Id}(&|$)"), new() { Timeout = PushMs });
            await Expect(Rows(page).Nth(1)).ToHaveAttributeAsync("aria-current", "true");
            await Expect(Rows(page).First).Not.ToHaveAttributeAsync("aria-current", "true");
            await Expect(page.GetByTestId("activity-detail")).ToContainTextAsync(secondName);
            await Expect(page.GetByTestId("activity-detail")).Not.ToContainTextAsync(firstName);

            await Rows(page).Nth(2).GetByRole(AriaRole.Button).ClickAsync();

            await Expect(page).ToHaveURLAsync(new Regex($"[?&]file={ordered[2].Id}(&|$)"), new() { Timeout = PushMs });
            await Expect(page).Not.ToHaveURLAsync(new Regex($"file={ordered[1].Id}(&|$)"));
            await Expect(page.GetByTestId("activity-detail")).ToContainTextAsync(thirdName);
        });
    }

    private async Task<IPage> OpenActivityAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync(Wide);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await page.GotoAsync($"{rig.BaseUrl}/activity");
        await Expect(Rows(page)).ToHaveCountAsync(MixedActivity.Statuses.Count, new() { Timeout = PushMs });
        return page;
    }

    private static ILocator Chip(IPage page, string name) =>
        page.GetByRole(AriaRole.Group, new() { Name = "Show" }).GetByRole(AriaRole.Button, new() { NameRegex = new($"^{Regex.Escape(name)}") });

    private static ILocator Rows(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "Files" }).GetByRole(AriaRole.Row).Filter(new() { HasTextRegex = FileName() });

    private static async Task<IReadOnlyList<string>> ListedAsync(IPage page) =>
        [.. (await Rows(page).AllInnerTextsAsync()).Select(text => FileName().Match(text).Value)];

    private static async Task<IReadOnlyList<string>> ServerOrderAsync(ProcessingRig rig, params (string Name, object Value)[] query) =>
        [.. (await ServerFilesAsync(rig, query)).Select(file => file.Name)];

    private static async Task<IReadOnlyList<(long Id, string Name)>> ServerFilesAsync(ProcessingRig rig, params (string Name, object Value)[] query)
    {
        (string, object)[] asked = query.Length == 0 ? [("sort", "when"), ("direction", "desc")] : query;
        var listed = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/files", asked);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return [.. listed.Fields["files"]!.AsArray().Select(file => ((long)file!["id"]!, Path.GetFileName((string)file["relative_path"]!)))];
    }
}
