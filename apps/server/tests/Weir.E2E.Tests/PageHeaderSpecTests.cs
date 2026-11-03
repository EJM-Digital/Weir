using System.Text.Json;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// The page header follows the spec Weir and Deluno share (Weir#1359, Deluno#1461), measured on the page as the browser draws it: the
/// eyebrow, the title, the Dashboard's top-card labels and a panel's title, each at exact pixel sizes and the first two at their offsets.
/// </summary>
public sealed class PageHeaderSpecTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;

    private const string Measure = """
        (selector) => {
          const element = document.querySelector(selector);
          if (!element) return null;
          const style = getComputedStyle(element);
          return {
            fontSize: style.fontSize,
            fontWeight: style.fontWeight,
            lineHeight: style.lineHeight,
            letterSpacing: style.letterSpacing,
            textTransform: style.textTransform,
            top: element.getBoundingClientRect().top,
          };
        }
        """;

    private static async Task<JsonElement> MeasureAsync(IPage page, string selector)
    {
        var measured = await page.EvaluateAsync<JsonElement?>(Measure, selector);
        Assert.True(measured is not null, $"{selector} is not on the page.");
        return measured.Value;
    }

    private static void ExpectStyle(JsonElement measured, string fontSize, string fontWeight, string lineHeight, string letterSpacing, string transform)
    {
        Assert.Equal(fontSize, measured.GetProperty("fontSize").GetString());
        Assert.Equal(fontWeight, measured.GetProperty("fontWeight").GetString());
        Assert.Equal(lineHeight, measured.GetProperty("lineHeight").GetString());
        Assert.Equal(letterSpacing, measured.GetProperty("letterSpacing").GetString());
        Assert.Equal(transform, measured.GetProperty("textTransform").GetString());
    }

    [E2EFact]
    public async Task The_header_and_the_Dashboards_labels_and_panel_titles_are_drawn_to_the_shared_spec()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await rig.WithEvidenceAsync(async () =>
        {
            var page = await NewPageAsync(new ViewportSize { Width = 2560, Height = 1300 });
            await Navigation.EnsureSignedInAsync(page, rig.BaseUrl);
            await Expect(page.GetByTestId("dashboard-live")).ToBeVisibleAsync(new() { Timeout = PushMs });
            await Expect(page.Locator(".mm-stat__label").First).ToBeVisibleAsync(new() { Timeout = PushMs });

            var eyebrow = await MeasureAsync(page, ".mm-header__eyebrow");
            ExpectStyle(eyebrow, "11px", "700", "14px", "1.54px", "uppercase");
            Assert.Equal(14d, eyebrow.GetProperty("top").GetDouble(), 0.5);

            var title = await MeasureAsync(page, ".mm-header__title");
            Assert.Equal("26px", title.GetProperty("fontSize").GetString());
            Assert.Equal("600", title.GetProperty("fontWeight").GetString());
            Assert.Equal("-0.78px", title.GetProperty("letterSpacing").GetString());
            Assert.Equal(32d, title.GetProperty("top").GetDouble(), 0.5);

            ExpectStyle(await MeasureAsync(page, ".mm-stat__label"), "11px", "600", "16px", "1.32px", "uppercase");

            var panel = await MeasureAsync(page, ".mm-panel__title");
            Assert.Equal("15px", panel.GetProperty("fontSize").GetString());
            Assert.Equal("600", panel.GetProperty("fontWeight").GetString());
            Assert.Equal("23px", panel.GetProperty("lineHeight").GetString());
        });
    }
}
