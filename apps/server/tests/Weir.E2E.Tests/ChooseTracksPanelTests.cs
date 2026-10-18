using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// Choose tracks by hand, for a file held for a person: the panel says it is reading a fresh probe while it does, tracks are reordered,
/// given default and forced flags, and what the page sends is what Weir writes. A file that cannot be read says so.
/// </summary>
public sealed class ChooseTracksPanelTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const float FinishMs = 60_000;
    private const string FileName = "Slow Burn 2019.mkv";

    // Index 0 is the video, 1 and 2 the English and Japanese audio, 3 the English subtitles.
    private const int EnglishAudio = 1;
    private const int JapaneseAudio = 2;
    private const int EnglishSubtitles = 3;

    private async Task<(IPage Page, RequestLog Log, ILocator Row)> OpenHeldFileAsync(ProcessingRig rig)
    {
        var page = await NewPageAsync();
        var log = RequestLog.Watch(page);
        await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
        await Navigation.OpenSidebarAsync(page, "Activity");
        var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = FileName });
        await rig.HandOffUnreadableAsync("h-held", FileName);
        await Expect(row).ToContainTextAsync("On hold", new() { Timeout = FinishMs });
        return (page, log, row);
    }

    private const string OrderNow = "() => [...document.querySelectorAll('[data-testid^=\"choose-tracks-order-\"]')].map((e) => e.dataset.testid.split('-').at(-1)).join()";

    private static async Task ExpectOrderAsync(IPage page, params int[] indices)
    {
        var want = string.Join(',', indices);
        try
        {
            await page.WaitForFunctionAsync($"(want) => ({OrderNow})() === want", want, new() { Timeout = PushMs });
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Wanted the track order {want}, but it is {await page.EvaluateAsync<string>(OrderNow)}.");
        }
    }

    private static async Task OpenChooseTracksAsync(IPage page, ILocator row)
    {
        await row.ClickAsync();
        await page.GetByTestId("activity-file-actions").GetByRole(AriaRole.Button, new() { Name = "Choose tracks", Exact = true }).ClickAsync();
    }

    [E2EFact]
    public async Task The_panel_reads_a_fresh_probe_then_reorders_tracks_and_sends_exactly_the_choice_that_Weir_writes()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log, row) = await OpenHeldFileAsync(rig);
            await rig.FinishArrivingAsync("h-held", FileName, FakeMedia.Probe(audioLanguages: ["eng", "jpn"], subtitleLanguages: ["eng"]), probeDelaySeconds: 2);

            await OpenChooseTracksAsync(page, row);

            var panel = page.GetByTestId("choose-tracks-panel");
            await Expect(panel).ToContainTextAsync("Reading a fresh probe of this file…", new() { Timeout = PushMs });
            await Expect(panel.GetByTestId($"choose-tracks-row-{EnglishSubtitles}")).ToBeVisibleAsync(new() { Timeout = PushMs });
            await Expect(panel).Not.ToContainTextAsync("Reading a fresh probe of this file…");

            // The English audio comes first; moving the Japanese earlier puts it first, and the order list says so.
            const int video = 0;
            await panel.GetByTestId($"choose-tracks-keep-{EnglishAudio}").SetCheckedAsync(true);
            await panel.GetByTestId($"choose-tracks-keep-{JapaneseAudio}").SetCheckedAsync(true);
            await panel.GetByTestId($"choose-tracks-keep-{EnglishSubtitles}").SetCheckedAsync(true);
            await ExpectOrderAsync(page, video, EnglishAudio, JapaneseAudio, EnglishSubtitles);
            await panel.GetByTestId($"choose-tracks-move-up-{JapaneseAudio}").ClickAsync();
            await ExpectOrderAsync(page, video, JapaneseAudio, EnglishAudio, EnglishSubtitles);
            await panel.GetByTestId($"choose-tracks-move-down-{JapaneseAudio}").ClickAsync();
            await ExpectOrderAsync(page, video, EnglishAudio, JapaneseAudio, EnglishSubtitles);
            await panel.GetByTestId($"choose-tracks-move-up-{JapaneseAudio}").ClickAsync();
            await ExpectOrderAsync(page, video, JapaneseAudio, EnglishAudio, EnglishSubtitles);

            await panel.GetByTestId($"choose-tracks-default-{JapaneseAudio}").CheckAsync();
            await panel.GetByTestId($"choose-tracks-forced-{EnglishSubtitles}").CheckAsync();
            var mark = log.Mark();
            await panel.GetByTestId("choose-tracks-submit").ClickAsync();

            await Expect(page.GetByTestId("activity-file-actions").GetByRole(AriaRole.Status)).ToContainTextAsync("Queued your track choice.", new() { Timeout = PushMs });
            var sent = Assert.Single(log.Since(mark));
            Assert.Equal("POST", sent.Method);
            var listed = (await rig.ListedFileAsync(FileName))!;
            Assert.Equal($"/api/v1/processing/files/{(long)listed["id"]!}/manual-plan", sent.PathAndQuery);
            var body = JsonNode.Parse(sent.Body!)!.AsObject();
            Assert.False(string.IsNullOrEmpty((string?)body["csrf_token"]), "The choice carries the page's CSRF token.");
            Assert.Equal([0, JapaneseAudio, EnglishAudio, EnglishSubtitles], body["order"]!.AsArray().Select(index => (int)index!));
            var keep = body["keep"]!.AsArray().Select(track => track!.AsObject()).ToDictionary(track => (int)track["index"]!);
            Assert.Equal(4, keep.Count);
            Assert.True((bool)keep[JapaneseAudio]["default"]!);
            Assert.False((bool)keep[EnglishAudio]["default"]!);
            Assert.True((bool)keep[EnglishSubtitles]["forced"]!);
            Assert.False((bool)keep[JapaneseAudio]["forced"]!);

            rig.ReleasePass(FileName);
            await Expect(row).ToContainTextAsync("Done", new() { Timeout = FinishMs });
            var written = string.Join(' ', rig.LatestRemuxArguments);
            Assert.True(
                written.IndexOf($"-map 0:{JapaneseAudio}", StringComparison.Ordinal) < written.IndexOf($"-map 0:{EnglishAudio}", StringComparison.Ordinal),
                $"Weir wrote the tracks in the chosen order: {written}");
            Assert.Contains($"-map 0:{EnglishSubtitles}", written, StringComparison.Ordinal);
        });
    }

    [E2EFact]
    public async Task A_file_that_can_no_longer_be_read_says_so_and_Escape_closes_the_panel_with_nothing_sent()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var (page, log, row) = await OpenHeldFileAsync(rig);
            rig.DeleteHandedOffFile("h-held", FileName);
            var mark = log.Mark();

            await OpenChooseTracksAsync(page, row);

            var panel = page.GetByTestId("choose-tracks-panel");
            await Expect(panel).ToBeVisibleAsync(new() { Timeout = PushMs });
            await Expect(panel.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Weir could not find this file under the saved watched folder.", new() { Timeout = PushMs });
            await page.Keyboard.PressAsync("Escape");
            await Expect(panel).ToHaveCountAsync(0);
            log.AssertNothingSentSince(mark);
        });
    }
}
