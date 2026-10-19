using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The update menu item has one click handler that does whatever the current state says, so the state table is
/// the whole behaviour: a repeated hourly check cannot stack a second handler on the item.
/// </summary>
public sealed class UpdateMenuStateTests
{
    [Fact]
    public void A_development_build_opens_the_web_update_check_instead()
    {
        var state = UpdateMenuState.Describe(isInstalled: false, UpdateActivity.Idle, hasPendingUpdate: true, isDownloaded: true, "3.3.0");

        Assert.Equal(new UpdateMenuState("Check for updates", true, UpdateMenuAction.OpenUpdatePage), state);
    }

    [Fact]
    public void Nothing_found_yet_offers_a_check()
    {
        var state = UpdateMenuState.Describe(isInstalled: true, UpdateActivity.Idle, hasPendingUpdate: false, isDownloaded: false, null);

        Assert.Equal(new UpdateMenuState("Check for updates", true, UpdateMenuAction.Check), state);
    }

    [Fact]
    public void A_found_update_offers_its_download()
    {
        var state = UpdateMenuState.Describe(isInstalled: true, UpdateActivity.Idle, hasPendingUpdate: true, isDownloaded: false, "3.3.0");

        Assert.Equal(new UpdateMenuState("Download update (v3.3.0)", true, UpdateMenuAction.Download), state);
    }

    [Fact]
    public void A_downloaded_update_offers_the_restart()
    {
        var state = UpdateMenuState.Describe(isInstalled: true, UpdateActivity.Idle, hasPendingUpdate: true, isDownloaded: true, "3.3.0");

        Assert.Equal(new UpdateMenuState("Restart to update (v3.3.0)", true, UpdateMenuAction.Restart), state);
    }

    [Theory]
    [InlineData((int)UpdateActivity.Checking, "Checking for updates...")]
    [InlineData((int)UpdateActivity.Downloading, "Downloading update...")]
    public void Work_in_progress_disables_the_item_whatever_was_found(int activity, string text)
    {
        var state = UpdateMenuState.Describe(isInstalled: true, (UpdateActivity)activity, hasPendingUpdate: true, isDownloaded: true, "3.3.0");

        Assert.Equal(new UpdateMenuState(text, false, UpdateMenuAction.Wait), state);
    }

    [Fact]
    public void A_failed_download_goes_back_to_offering_the_download()
    {
        // The item said "Downloading update..." while it ran; once idle again with nothing downloaded, it offers
        // the download again rather than staying disabled.
        var during = UpdateMenuState.Describe(isInstalled: true, UpdateActivity.Downloading, hasPendingUpdate: true, isDownloaded: false, "3.3.0");
        var after = UpdateMenuState.Describe(isInstalled: true, UpdateActivity.Idle, hasPendingUpdate: true, isDownloaded: false, "3.3.0");

        Assert.False(during.Enabled);
        Assert.Equal(UpdateMenuAction.Download, after.Action);
        Assert.True(after.Enabled);
    }
}
