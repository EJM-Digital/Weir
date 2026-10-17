using System.Text.Json.Nodes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

public sealed class ProcessingActivityDetailTests(E2EServer server) : E2ETestBase(server)
{
    [E2EFact]
    public async Task Processing_activity_card_shows_before_after_processing_details()
    {
        var detail = new JsonObject
        {
            ["outcome"] = "live_output_written",
            ["ok"] = true,
            ["media_scope"] = "movie",
            ["relative_media_path"] = "movies/Example.Movie.2024.mkv",
            ["inspected_source_path"] = @"C:\Media\Movies\Example.Movie.2024.mkv",
            ["output_file"] = @"C:\Media\Movies-Output\Example.Movie.2024.mkv",
            ["stream_counts"] = new JsonObject { ["video"] = 1, ["audio"] = 9, ["subtitle"] = 8 },
            ["audio_before"] = "eng DTS-HD MA 5.1; jpn AAC 2.0; spa AC3 5.1; fre AC3 5.1; deu AC3 5.1; ita AC3 5.1; por AC3 5.1; nld AC3 5.1",
            ["audio_after"] = "eng DTS-HD MA 5.1; jpn AAC 2.0",
            ["subs_before"] = "eng full; eng forced; spa full; fre full; deu full; ita full; por full; jpn signs",
            ["subs_after"] = "eng full; eng forced; jpn signs",
            ["removed_audio"] = new JsonArray("spa AC3 5.1", "fre AC3 5.1", "deu AC3 5.1", "ita AC3 5.1", "por AC3 5.1", "nld AC3 5.1"),
            ["removed_subtitles"] = new JsonArray("spa full", "fre full", "deu full", "ita full", "por full"),
            ["plan_summary"] = "Video copied. Audio and subtitle tracks trimmed to preferred languages.",
            ["remux_required"] = true,
            ["source_size_bytes"] = 5368709120L,
            ["output_size_bytes"] = 4294967296L,
            ["source_folder_deleted"] = false,
            ["source_folder_skip_reason"] = "Source folder retained.",
            ["movie_output_folder_deleted"] = false,
            ["movie_output_folder_skip_reason"] = "Output title folder retained.",
            ["ffmpeg_argv"] = new JsonArray("ffmpeg", "-i", "Example.Movie.2024.mkv", "-map", "0", "Example.Movie.2024.out.mkv"),
        };
        E2EDatabase.InsertActivityEvent(
            Server.DatabasePath,
            eventType: "processing.file_remux_pass_completed",
            module: "processing",
            title: "Remux finished for Example.Movie.2024.mkv",
            detail: detail.ToJsonString());

        var page = await NewPageAsync();

        await Navigation.EnsureSignedInAsync(page, BaseUrl);
        await Navigation.OpenLogsAsync(page);

        // The row opens to the before/after detail of the pass.
        var title = page.GetByText("Example.Movie.2024.mkv was processed successfully", new() { Exact = true });
        await Expect(title).ToBeVisibleAsync();
        await title.ClickAsync();
        var detailCard = page.GetByTestId("processing-remux-activity-detail");
        await Expect(detailCard).ToBeVisibleAsync();
        await Expect(detailCard.Locator(".mm-activity-remux-detail__tile-label").Filter(new() { HasTextString = "Original size" })).ToBeVisibleAsync();
        await Expect(detailCard.Locator(".mm-activity-remux-detail__tile-label").Filter(new() { HasTextString = "Final size" })).ToBeVisibleAsync();
        await Expect(detailCard.Locator(".mm-activity-remux-detail__tile-label").Filter(new() { HasTextString = "Change" })).ToBeVisibleAsync();
        await detailCard.GetByText("Show track and cleanup details", new() { Exact = true }).ClickAsync();
        await Expect(detailCard.GetByText("Before", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailCard.GetByText("After", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailCard.GetByText("Audio in file", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailCard.GetByText("Audio kept", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailCard.GetByText("Audio removed", new() { Exact = true })).ToBeVisibleAsync();
        await Axe.AssertNoViolationsAsync(page, "A log row opened to its track and cleanup detail");
        await Expect(detailCard.GetByText("Subtitles in file", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailCard.GetByText("Subtitles kept", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailCard.GetByText("Subtitles removed", new() { Exact = true })).ToBeVisibleAsync();
        Assert.True(await detailCard.GetByText("nld AC3 5.1", new() { Exact = true }).CountAsync() >= 2);
        Assert.True(await detailCard.GetByText("por full", new() { Exact = true }).CountAsync() >= 2);
        Assert.Equal(0, await detailCard.GetByText("Show 2 more", new() { Exact = true }).CountAsync());
        await Expect(detailCard.GetByText(@"C:\Media\Movies\Example.Movie.2024.mkv", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailCard.GetByText(@"C:\Media\Movies-Output\Example.Movie.2024.mkv", new() { Exact = true })).ToBeVisibleAsync();
    }
}
