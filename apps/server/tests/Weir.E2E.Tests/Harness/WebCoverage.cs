using Microsoft.Playwright;

namespace Weir.E2E.Tests.Harness;

/// <summary>
/// Collects what the web app ran in the browser, when the app was built with <c>WEIR_WEB_COVERAGE=1</c> (apps/web/vite.config.ts), so
/// the browser tests count towards the web app's coverage (docs/coverage/README.md). Each test writes what its pages reported to
/// <c>$WEIR_WEB_COVERAGE_DIR/&lt;Class.Method&gt;.json</c>, a JSON array of Istanbul coverage objects; <c>scripts/merge-web-coverage.mjs</c> merges them.
/// A page's coverage lives and dies with its document, so a page reports when it is left (the <c>pagehide</c> event) and the
/// pages still open when the test ends are read directly.
/// </summary>
public static class WebCoverage
{
    private const string FolderVariable = "WEIR_WEB_COVERAGE_DIR";
    private const string Binding = "weirReportCoverage";

    private static readonly string? Folder = Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } folder ? folder : null;

    public static bool Enabled => Folder is not null;

    /// <summary>Makes every page of the context report its coverage into <paramref name="reports"/> when it is left.</summary>
    public static async Task AttachAsync(IBrowserContext context, List<string> reports)
    {
        if (!Enabled)
        {
            return;
        }

        await context.ExposeBindingAsync(Binding, (BindingSource _, string report) =>
        {
            lock (reports)
            {
                reports.Add(report);
            }
        });
        await context.AddInitScriptAsync($"addEventListener('pagehide', () => {{ if (window.__coverage__) window.{Binding}(JSON.stringify(window.__coverage__)); }});");
    }

    /// <summary>Writes the reports of left pages and of the pages still open in <paramref name="contexts"/> to the test's file.</summary>
    public static async Task WriteAsync(string test, IEnumerable<IBrowserContext> contexts, List<string> reports)
    {
        if (Folder is null)
        {
            return;
        }

        var all = new List<string>();
        lock (reports)
        {
            all.AddRange(reports);
        }

        foreach (var page in contexts.SelectMany(context => context.Pages))
        {
            try
            {
                var report = await page.EvaluateAsync<string?>("() => window.__coverage__ ? JSON.stringify(window.__coverage__) : null");
                if (report is not null)
                {
                    all.Add(report);
                }
            }
            catch (PlaywrightException)
            {
                // A page that is already closed has reported when it was left.
            }
        }

        if (all.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(Folder);
        await File.WriteAllTextAsync(Path.Combine(Folder, $"{test}.json"), $"[{string.Join(',', all)}]");
    }
}
