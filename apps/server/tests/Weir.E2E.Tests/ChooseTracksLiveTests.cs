using Microsoft.Playwright;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>A file held for a person is opened in Activity, its tracks are chosen by hand, and Weir cleans it the way they chose.</summary>
public sealed class ChooseTracksLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const float FinishMs = 30_000;
    private const string FileName = "Slow Burn 2019.mkv";

    // Index 0 is the video, 1 and 2 the English and Japanese audio, 3 the English subtitles.
    private const int EnglishAudio = 1;
    private const int JapaneseAudio = 2;
    private const int EnglishSubtitles = 3;

    [E2EFact]
    public async Task Choosing_tracks_of_a_held_file_reads_its_tracks_and_Weir_cleans_the_file_as_chosen()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await NewPageAsync();
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            await Navigation.OpenSidebarAsync(page, "Activity");
            var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = FileName });
            await rig.HandOffUnreadableAsync("h-held", FileName);
            await Expect(row).ToContainTextAsync("On hold", new() { Timeout = FinishMs });
            await rig.FinishArrivingAsync("h-held", FileName, FakeMedia.Probe(audioLanguages: ["eng", "jpn"], subtitleLanguages: ["eng"]));

            await row.ClickAsync();
            var actions = page.GetByTestId("activity-file-actions");
            await actions.GetByRole(AriaRole.Button, new() { Name = "Choose tracks", Exact = true }).ClickAsync();

            var panel = page.GetByTestId("choose-tracks-panel");
            await Expect(panel.GetByTestId($"choose-tracks-row-{EnglishSubtitles}")).ToBeVisibleAsync(new() { Timeout = PushMs });
            await panel.GetByTestId($"choose-tracks-keep-{EnglishAudio}").SetCheckedAsync(true);
            await panel.GetByTestId($"choose-tracks-keep-{JapaneseAudio}").SetCheckedAsync(true);
            await panel.GetByTestId($"choose-tracks-keep-{EnglishSubtitles}").SetCheckedAsync(false);
            await panel.GetByTestId("choose-tracks-submit").ClickAsync();

            await Expect(actions.GetByRole(AriaRole.Status)).ToContainTextAsync("Queued your track choice.", new() { Timeout = PushMs });
            await Expect(panel).ToHaveCountAsync(0);
            rig.ReleasePass(FileName);
            await Expect(row).ToContainTextAsync("Done", new() { Timeout = FinishMs });
            Assert.Equal(1, rig.RemuxCount);
            var kept = string.Join(' ', rig.LatestRemuxArguments);
            Assert.Contains($"-map 0:{EnglishAudio}", kept, StringComparison.Ordinal);
            Assert.Contains($"-map 0:{JapaneseAudio}", kept, StringComparison.Ordinal);
            Assert.DoesNotContain($"-map 0:{EnglishSubtitles}", kept, StringComparison.Ordinal);
        });
    }
}
