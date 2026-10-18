using Xunit;

namespace Weir.Tray.Tests;

/// <summary>What tray-host.log keeps of a balloon, so a notification Windows never showed is still on record.</summary>
public sealed class TrayBalloonLogTests
{
    [Theory]
    [InlineData(ToolTipIcon.Info, "Balloon (info) Weir: Weir is running at http://localhost:9347. Click to open it.")]
    [InlineData(ToolTipIcon.Warning, "Balloon (warning) Weir: Weir is running at http://localhost:9347. Click to open it.")]
    [InlineData(ToolTipIcon.Error, "Balloon (error) Weir: Weir is running at http://localhost:9347. Click to open it.")]
    public void A_balloon_is_logged_with_its_icon_title_and_the_words_the_person_was_shown(ToolTipIcon icon, string expected)
    {
        Assert.Equal(expected, TrayBalloons.LogLine("Weir", TrayBalloons.RunningText(9347), icon));
    }

    [Fact]
    public void An_update_balloon_keeps_its_own_title()
    {
        Assert.Equal(
            "Balloon (info) Weir Update: Version 3.4.0 is available. Open System › About in Weir to update.",
            TrayBalloons.LogLine("Weir Update", "Version 3.4.0 is available. Open System › About in Weir to update.", ToolTipIcon.Info));
    }
}
