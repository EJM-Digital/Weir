using System.Text.Json;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>The accessibility check (axe-core, the checker the web app's own tests use) run on the page as it is shown.</summary>
public static class Axe
{
    private const string Run =
        """
        async () => (await window.axe.run(document)).violations.map(v => ({
            id: v.id,
            impact: v.impact,
            help: v.help,
            targets: v.nodes.map(n => n.target.join(' ')),
        }))
        """;

    /// <summary>Fails, naming each rule and the elements that break it, when axe finds a violation on the page now. <paramref name="state"/> says which state it was.</summary>
    public static async Task AssertNoViolationsAsync(IPage page, string state)
    {
        // Evaluated rather than added as a script tag, so the page's content security policy does not stop it.
        await page.EvaluateAsync($"{await File.ReadAllTextAsync(RepoPaths.AxeCore)}\n;undefined");
        var found = await page.EvaluateAsync<JsonElement>(Run);
        var lines = found.EnumerateArray().Select(violation =>
            $"{violation.GetProperty("id").GetString()} ({violation.GetProperty("impact").GetString()}): {violation.GetProperty("help").GetString()} "
            + $"at {string.Join(" | ", violation.GetProperty("targets").EnumerateArray().Select(target => target.GetString()))}");
        Assert.True(!lines.Any(), $"{state} breaks accessibility rules:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }
}
