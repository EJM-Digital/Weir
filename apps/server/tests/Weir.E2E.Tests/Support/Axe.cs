using System.Text.Json;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>The accessibility check (axe-core, the checker the web app's own tests use) run on the page as it is shown.</summary>
public static class Axe
{
    private const string Run =
        """
        async () => {
            // A colour that is part-way through a transition is not the colour the page ends up with.
            await Promise.all(document.getAnimations()
                .filter(a => Number.isFinite(a.effect?.getComputedTiming().endTime))
                .map(a => a.finished.catch(() => undefined)));
            return (await window.axe.run(document)).violations.map(v => ({
                id: v.id,
                impact: v.impact,
                help: v.help,
                targets: v.nodes.map(n => n.target.join(' ') + (n.any[0] ? ' [' + n.any[0].message + ']' : '')),
            }));
        }
        """;

    /// <summary>Fails, naming each rule and the elements that break it, when axe finds a violation on the page now. <paramref name="state"/> says which state it was.</summary>
    public static async Task AssertNoViolationsAsync(IPage page, string state)
    {
        var lines = await ViolationsAsync(page);
        Assert.True(lines.Count == 0, $"{state} breaks accessibility rules:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    /// <summary>One line for each rule axe finds broken on the page now, naming the rule and the elements that break it. Empty when the page passes.</summary>
    public static async Task<IReadOnlyList<string>> ViolationsAsync(IPage page)
    {
        // Evaluated rather than added as a script tag, so the page's content security policy does not stop it.
        await page.EvaluateAsync($"{await File.ReadAllTextAsync(RepoPaths.AxeCore)}\n;undefined");
        var found = await page.EvaluateAsync<JsonElement>(Run);
        return
        [
            .. found.EnumerateArray().Select(violation =>
                $"{violation.GetProperty("id").GetString()} ({violation.GetProperty("impact").GetString()}): {violation.GetProperty("help").GetString()} "
                + $"at {string.Join(" | ", violation.GetProperty("targets").EnumerateArray().Select(target => target.GetString()))}"),
        ];
    }
}
