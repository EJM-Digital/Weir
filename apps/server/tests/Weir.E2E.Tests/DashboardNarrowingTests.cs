using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The header's two choices narrow every part of Live. One workflow, Movies, has two cleaned films, a film being written and a link to
/// Deluno, and TV has nothing, so what each part shows says which workflow and which kind of work it is looking at.
/// </summary>
public sealed class DashboardNarrowingTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const float PassMs = 60_000;
    private const string Working = "Harbour Lights 2024.mkv";
    private static readonly ViewportSize Roomy = new() { Width = 1600, Height = 1000 };

    private static async Task<ProcessingRig> StartBusyRigAsync()
    {
        var rig = await ProcessingRig.StartAsync();
        try
        {
            await rig.PlaceFileAsync("r-alpha", "Alpha 2001.mkv");
            await rig.PlaceFileAsync("r-beta", "Beta 2002.mkv");
            await rig.ScanAsync();
            await rig.WaitForStatusAsync("Alpha 2001.mkv", "processed");
            await rig.WaitForStatusAsync("Beta 2002.mkv", "processed");
            await rig.LinkMoviesToDelunoAsync();
            await rig.HandOffAsync("h-harbour", Working);
            await rig.WaitUntilWorkingAsync("h-harbour");
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private async Task<IPage> OpenLiveAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync(Roomy);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await Expect(page.GetByTestId("processing-page")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1", new() { Timeout = PushMs });
        await Expect(page.GetByTestId("live-done-today")).ToHaveTextAsync("2", new() { Timeout = PassMs });
        return page;
    }

    private static async Task ChooseWorkflowAsync(IPage page, string name)
    {
        await page.GetByTestId("workflow-picker").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("workflow-picker")).ToContainTextAsync(name);
    }

    private static ILocator HealthRows(IPage page, string area) =>
        page.GetByTestId("live-health").GetByRole(AriaRole.Region, new() { Name = area, Exact = true }).GetByRole(AriaRole.Listitem);

    [E2EFact]
    public async Task The_workflow_choice_narrows_every_part_of_Live_and_its_address_and_All_workflows_brings_them_back()
    {
        await using var rig = await StartBusyRigAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenLiveAsync(rig);
            var tv = await WorkflowIdAsync(rig, "tv");
            var movies = await rig.MoviesWorkflowIdAsync();
            var asked = new List<string>();
            page.Request += (_, request) =>
            {
                lock (asked)
                {
                    asked.Add(request.Url);
                }
            };
            await Expect(page.GetByTestId("pipeline-board")).ToContainTextAsync("Harbour Lights");
            await Expect(HealthRows(page, "Workflows")).ToHaveCountAsync(2);
            await Expect(HealthRows(page, "Connections")).ToHaveCountAsync(1);

            await ChooseWorkflowAsync(page, "TV");

            await Expect(page).ToHaveURLAsync(new Regex($@"[?&]workflow={tv}(&|$)"));
            await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("0", new() { Timeout = PushMs });
            await Expect(page.GetByTestId("pipeline-board")).ToContainTextAsync("Nothing in progress. New downloads appear here.");
            await Expect(page.GetByTestId("live-done-today")).ToHaveTextAsync("0", new() { Timeout = PushMs });
            await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Just finished" })).Not.ToContainTextAsync("Alpha");
            await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Activity", Exact = true }).Last).Not.ToContainTextAsync("Alpha (2001)");
            await Expect(NeedsYouPanel.Of(page)).ToContainTextAsync("Nothing needs you.");
            await Expect(HealthRows(page, "Workflows")).ToHaveCountAsync(1);
            await Expect(HealthRows(page, "Workflows")).ToContainTextAsync("TV");
            await Expect(page.GetByTestId("live-health")).ToContainTextAsync("Nothing connected.");
            lock (asked)
            {
                Assert.Contains(asked, url => url.Contains("/api/v1/activity/recent", StringComparison.Ordinal) && url.Contains($"library_id={tv}", StringComparison.Ordinal));
            }

            await ChooseWorkflowAsync(page, "Movies");

            await Expect(page).ToHaveURLAsync(new Regex($@"[?&]workflow={movies}(&|$)"));
            await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1", new() { Timeout = PushMs });
            await Expect(page.GetByTestId("live-done-today")).ToHaveTextAsync("2", new() { Timeout = PushMs });
            await Expect(page.GetByTestId("pipeline-board")).ToContainTextAsync("Harbour Lights");
            await Expect(HealthRows(page, "Workflows")).ToHaveCountAsync(1);
            await Expect(HealthRows(page, "Workflows")).ToContainTextAsync("Movies");
            await Expect(HealthRows(page, "Connections")).ToHaveCountAsync(1);

            await ChooseWorkflowAsync(page, "All workflows");

            await Expect(page).Not.ToHaveURLAsync(new Regex("[?&]workflow="));
            await Expect(HealthRows(page, "Workflows")).ToHaveCountAsync(2);
            await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1");
        });
    }

    [E2EFact]
    public async Task The_kind_of_work_choice_narrows_every_part_of_Live_and_its_address_and_Everything_brings_them_back()
    {
        await using var rig = await StartBusyRigAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await OpenLiveAsync(rig);
            var kinds = page.GetByRole(AriaRole.Group, new() { Name = "Show work from" });
            await Expect(kinds.GetByRole(AriaRole.Button, new() { Name = "Everything", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");

            await kinds.GetByRole(AriaRole.Button, new() { Name = "Library cleaning", Exact = true }).ClickAsync();

            await Expect(page).ToHaveURLAsync(new Regex("[?&]work=library(&|$)"));
            await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("0", new() { Timeout = PushMs });
            await Expect(page.GetByTestId("pipeline-board")).ToContainTextAsync("No library cleaning right now.");
            await Expect(NeedsYouPanel.Of(page)).ToContainTextAsync("Nothing from library cleaning.");
            await Expect(page.GetByTestId("live-done-today")).ToHaveTextAsync("0", new() { Timeout = PushMs });
            await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Today" })).Not.ToContainTextAsync("saved");
            await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Next" })).ToContainTextAsync("No library cleaning is scheduled.");
            await Expect(kinds.GetByRole(AriaRole.Button, new() { Name = "Library cleaning", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");

            await kinds.GetByRole(AriaRole.Button, new() { Name = "New downloads", Exact = true }).ClickAsync();

            await Expect(page).ToHaveURLAsync(new Regex("[?&]work=download(&|$)"));
            await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1", new() { Timeout = PushMs });
            await Expect(page.GetByTestId("pipeline-board")).ToContainTextAsync("Harbour Lights");
            await Expect(page.GetByTestId("live-done-today")).ToHaveTextAsync("2", new() { Timeout = PushMs });
            await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Next" })).ToContainTextAsync("No new downloads are due to be looked for.");

            await kinds.GetByRole(AriaRole.Button, new() { Name = "Everything", Exact = true }).ClickAsync();

            await Expect(page).Not.ToHaveURLAsync(new Regex("[?&]work="));
            await Expect(page.GetByTestId("live-working-count")).ToHaveTextAsync("1");
        });
    }

    private static async Task<long> WorkflowIdAsync(ProcessingRig rig, string mediaType)
    {
        var listed = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/libraries");
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return (long)listed.Elements.Select(row => row!.AsObject()).First(row => (string?)row["media_type"] == mediaType)["id"]!;
    }
}
