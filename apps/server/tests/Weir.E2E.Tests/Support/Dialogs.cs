using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>What a person does to a dialog other than pressing one of its buttons.</summary>
public static class Dialogs
{
    private const float Margin = 10;

    /// <summary>
    /// A click on the dimmed area beside the card. The sidebar sits above the dialog's layer on the left, so the click goes to the
    /// top right-hand corner.
    /// </summary>
    public static async Task ClickOutsideAsync(ILocator dialog)
    {
        var layer = (await dialog.BoundingBoxAsync())!;
        await dialog.ClickAsync(new() { Position = new() { X = layer.Width - Margin, Y = Margin } });
    }
}
