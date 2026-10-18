using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// Notes inside the page that something was true at some moment, so a state that lasts only until the server answers (a button that reads
/// "Saving…", one that is off while it works) is not missed between two looks. The page looks at every change it makes to itself.
/// </summary>
public static class PageTrail
{
    /// <summary>From now on, notes under <paramref name="name"/> whether <paramref name="condition"/>, a JavaScript expression about the page, was ever true.</summary>
    public static async Task WatchAsync(IPage page, string name, string condition) =>
        await page.EvaluateAsync(
            """
            ([name, condition]) => {
              const seen = (window.__weirTrail ??= {});
              seen[name] = false;
              const holds = new Function(`return (${condition});`);
              const look = () => { if (!seen[name] && holds()) seen[name] = true; };
              new MutationObserver(look).observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true });
              look();
            }
            """,
            new object[] { name, condition });

    /// <summary>From now on, notes under <paramref name="text"/> whether that text was ever on the page.</summary>
    public static Task WatchTextAsync(IPage page, string text) =>
        WatchAsync(page, text, $"document.body.textContent.includes({System.Text.Json.JsonSerializer.Serialize(text)})");

    /// <summary>Whether the watch named <paramref name="name"/> ever saw its condition hold.</summary>
    public static async Task<bool> SawAsync(IPage page, string name) =>
        await page.EvaluateAsync<bool>("(name) => window.__weirTrail?.[name] === true", name);
}
