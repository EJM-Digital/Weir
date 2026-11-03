using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A release that was cleaned, then sent again after its folder had left the watched folder: the second send is looked at twice and
/// finds nothing, and the hand-off ends there with the manager told the download is gone. Sent once more, it is answered again
/// from that same ending, never left queued.
/// </summary>
public sealed partial class HeldFileReturnTests
{
    private async Task CleanThenSendAgainAfterTheFolderHasGoneAsync()
    {
        await SetUpAsync(releaseFolder: true);
        _folders.Source($"{ReleaseName}/Featurettes/Gallery.mkv");
        await HandOffAsync("first");
        await DrainAsync();
        Assert.Equal(HandoffLedgerRules.Completed, (await HandoffStatusAsync("first")).State);

        Directory.Delete(Moved, recursive: true);
        await HandOffAsync("first");
        await DrainAsync();
        Advance(GoneSources.LookAgainAfter + TimeSpan.FromMinutes(1));
        await DrainAsync();
        await FlushOwedReportsAsync();
    }

    [Fact]
    public async Task A_release_sent_again_after_its_folder_has_gone_ends_as_gone_once_both_looks_find_nothing()
    {
        await CleanThenSendAgainAfterTheFolderHasGoneAsync();

        var status = await HandoffStatusAsync("first");
        Assert.Equal(HandoffLedgerRules.Failed, status.State);
        var report = Assert.Single(ReportsFor("first"), candidate => WireConvert.Str(candidate["status"]) == "failed");
        Assert.Equal("source_gone", WireConvert.Str(report["failureClass"]));
    }

    [Fact]
    public async Task A_release_whose_gone_ending_was_reported_is_not_reopened_by_a_further_send()
    {
        await CleanThenSendAgainAfterTheFolderHasGoneAsync();

        await HandOffAsync("first");
        await DrainAsync();
        Advance(GoneSources.LookAgainAfter + TimeSpan.FromMinutes(1));
        await DrainAsync();
        await FlushOwedReportsAsync();

        Assert.NotEqual(HandoffLedgerRules.Queued, (await HandoffStatusAsync("first")).State);
        Assert.Equal(HandoffLedgerRules.Failed, (await HandoffStatusAsync("first")).State);
    }
}
