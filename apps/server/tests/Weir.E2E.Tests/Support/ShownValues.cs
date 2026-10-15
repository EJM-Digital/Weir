using System.Text.Json;
using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// The numbers a screen shows for something that changes while a test waits, recorded in the page itself as they appear. A test that
/// looked at the screen every so often would miss a value that came and went between two looks; the page sees every change.
/// </summary>
public static class ShownValues
{
    /// <summary>
    /// Starts recording. Every distinct number captured by <paramref name="pattern"/> (a JavaScript pattern with one group, the number
    /// written with thousands separators or not) in the text of the first element of <paramref name="selector"/> that matches it is
    /// kept, in the order the screen showed them.
    /// </summary>
    public static Task StartAsync(IPage page, string name, string selector, string pattern) =>
        page.EvaluateAsync(
            """
            ({ name, selector, pattern }) => {
              const seen = (window[name] = []);
              const expression = new RegExp(pattern);
              const look = () => {
                for (const element of document.querySelectorAll(selector)) {
                  if (selector === '*' && element.children.length > 0) continue;
                  const found = expression.exec(element.innerText ?? '');
                  if (!found) continue;
                  const shown = Number(found[1].replace(/,/g, ''));
                  if (seen.length === 0 || seen[seen.length - 1] !== shown) seen.push(shown);
                  return;
                }
              };
              new MutationObserver(look).observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true });
              look();
            }
            """,
            new { name, selector, pattern });

    /// <summary>What the screen has shown so far, in order, for a recording that <see cref="StartAsync"/> began.</summary>
    public static async Task<List<int>> ReadAsync(IPage page, string name)
    {
        var seen = await page.EvaluateAsync<JsonElement>("(name) => window[name]", name);
        return [.. seen.EnumerateArray().Select(value => value.GetInt32())];
    }

    /// <summary>Waits, for no longer than <paramref name="timeoutMs"/>, until the recording has seen <paramref name="count"/> and then reads it.</summary>
    public static async Task<List<int>> UntilAsync(IPage page, string name, int count, float timeoutMs)
    {
        await page.WaitForFunctionAsync(
            "({ name, count }) => window[name].includes(count)",
            new { name, count },
            new() { Timeout = timeoutMs });
        return await ReadAsync(page, name);
    }
}
