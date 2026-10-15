using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>Clicking the dimmed area around a dialog, which is the dialog's own outer element.</summary>
public static class Backdrop
{
    /// <summary>
    /// Finds a point of <paramref name="dialog"/> that is not on its card and clicks it. The side menu sits above the dimmed area at
    /// the left, so each corner is asked what is there first, and the click goes only to one where it is the dialog itself: a click
    /// that landed on anything else would prove nothing about the backdrop.
    /// </summary>
    public static async Task ClickAsync(IPage page, ILocator dialog)
    {
        var point = await dialog.EvaluateAsync<double[]?>(
            """
            element => {
              const inset = 6;
              const box = element.getBoundingClientRect();
              const corners = [
                [box.right - inset, box.bottom - inset],
                [box.right - inset, box.top + inset],
                [box.left + inset, box.bottom - inset],
                [box.left + inset, box.top + inset],
              ];
              return corners.find(([x, y]) => document.elementFromPoint(x, y) === element) ?? null;
            }
            """);
        if (point is null)
        {
            throw new InvalidOperationException("No corner of the dialog's backdrop can be clicked: something else covers each.");
        }

        await page.Mouse.ClickAsync((float)point[0], (float)point[1]);
    }
}
