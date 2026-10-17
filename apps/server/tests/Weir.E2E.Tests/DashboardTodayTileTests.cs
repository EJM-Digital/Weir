using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The Today tile counts what Weir cleaned and the space that saved, and its chart counts the files that finished in the last two hours,
/// each file once, by how it ended now. Both are held to the figures the server gives for the same time.
/// </summary>
public sealed partial class DashboardTodayTileTests(E2EServer server) : E2ETestBase(server)
{
    private const float PassMs = 60_000;
    private const string PassCompleted = "processing.file_remux_pass_completed";

    [GeneratedRegex(@"^([\d.,]+) (B|KB|MB|GB|TB) saved$")]
    private static partial Regex SavedWords();

    private static double BytesOf(string words)
    {
        var match = SavedWords().Match(words);
        Assert.True(match.Success, $"'{words}' is not a saving.");
        var unit = Array.IndexOf(["B", "KB", "MB", "GB", "TB"], match.Groups[2].Value);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * Math.Pow(1024, unit);
    }

    private async Task<(IPage Page, ProcessingRig Rig)> OpenWithTwoCleanedFilmsAsync()
    {
        var rig = await ProcessingRig.StartAsync();
        try
        {
            await rig.PlaceFileAsync("r-alpha", "Alpha 2001.mkv");
            await rig.PlaceFileAsync("r-beta", "Beta 2002.mkv");
            await rig.ScanAsync();
            await rig.WaitForStatusAsync("Alpha 2001.mkv", "processed");
            await rig.WaitForStatusAsync("Beta 2002.mkv", "processed");
            var page = await NewPageAsync();
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            return (page, rig);
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    [E2EFact]
    public async Task Cleaned_and_saved_are_the_servers_totals_and_the_chart_counts_the_files_that_finished()
    {
        var (page, rig) = await OpenWithTwoCleanedFilmsAsync();
        await using var _ = rig;
        await rig.WithEvidenceAsync(async () =>
        {
            var stats = await rig.Admin.GetAsync($"{WeirClient.Api}/processing/overview-stats", ("window_days", 1));
            Assert.True(stats.Status == HttpStatusCode.OK, stats.ToString());
            Assert.Equal(2, (int)stats.Fields["files_processed"]!);
            var saved = (double)stats.Fields["net_space_saved_bytes"]!;
            Assert.True(saved > 0, "The two films should have saved some space.");

            var today = page.GetByRole(AriaRole.Region, new() { Name = "Today", Exact = true });
            await Expect(page.GetByTestId("live-done-today")).ToHaveTextAsync("2", new() { Timeout = PassMs });
            var shown = await today.Locator(".mm-stat__side").InnerTextAsync();
            Assert.InRange(BytesOf(shown), saved * 0.95, saved * 1.05);
            await Expect(page.GetByTestId("live-handed-back-sum")).ToHaveTextAsync("2 files finished in the last 2 hours: 2 cleaned.");
            var finished = await rig.Admin.GetAsync($"{WeirClient.Api}/activity/recent", ("event_type", PassCompleted));
            Assert.True(finished.Status == HttpStatusCode.OK, finished.ToString());
            Assert.Equal(2, (int)finished.Fields["total"]!);
        });
    }

    [E2EFact]
    public async Task A_film_that_finished_twice_is_counted_once_in_the_chart_by_how_it_ended_last()
    {
        var (page, rig) = await OpenWithTwoCleanedFilmsAsync();
        await using var _ = rig;
        await rig.WithEvidenceAsync(async () =>
        {
            await Expect(page.GetByTestId("live-handed-back-sum")).ToHaveTextAsync("2 files finished in the last 2 hours: 2 cleaned.", new() { Timeout = PassMs });
            var alpha = (await rig.ListedFileAsync("Alpha 2001.mkv"))!;
            var again = await rig.Admin.PostWithCsrfAsync($"{WeirClient.Api}/processing/files/{(long)alpha["id"]!}/requeue");
            Assert.True(again.Status == HttpStatusCode.OK, again.ToString());

            // Alpha now has two entries in Activity, its pass and the repeat that was skipped, yet it is one film in the chart.
            await Poll.UntilAsync(
                async () => (int)(await rig.Admin.GetAsync($"{WeirClient.Api}/activity/recent", ("event_type", "processing.file_skipped_repeat"))).Fields["total"]! >= 1,
                "the repeat of Alpha to be written to Activity",
                TimeSpan.FromMilliseconds(PassMs));

            await Expect(page.GetByTestId("live-handed-back-sum")).ToHaveTextAsync("2 files finished in the last 2 hours: 2 cleaned.");
            await Expect(page.GetByTestId("live-done-today")).ToHaveTextAsync("2");
        });
    }
}
