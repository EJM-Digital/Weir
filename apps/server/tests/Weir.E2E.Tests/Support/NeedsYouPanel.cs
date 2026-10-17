using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>Finding the Dashboard's Needs you panel and the row of one file in it.</summary>
public static class NeedsYouPanel
{
    public static ILocator Of(IPage page) => page.GetByRole(AriaRole.Region, new() { Name = "Needs you" });

    /// <summary>The row of the file whose shown title contains <paramref name="title"/>.</summary>
    public static ILocator RowOf(IPage page, string title) =>
        page.GetByTestId("live-needs").GetByRole(AriaRole.Listitem).Filter(new() { HasText = title });

    public static ILocator Notice(IPage page) => Of(page).GetByRole(AriaRole.Status);
}
