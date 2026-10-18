using System.Globalization;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// A browser test: it starts from the setup page (the previous test's users and settings are cleared), gets a
/// headless Chromium of its own, and closes it afterwards. Every page is traced and its console, failed requests and
/// error responses logged; <see cref="FailureArtifacts"/> says what is kept when the test fails.
/// </summary>
[Collection(SharedServer.Name)]
public abstract class E2ETestBase(E2EServer server) : IAsyncLifetime
{
    private const float DefaultTimeoutMs = 30_000;
    private const int ServerLogLines = 2_000;

    private readonly List<IBrowserContext> _contexts = [];
    private readonly List<string> _browserLog = [];
    private IBrowser? _browser;

    protected E2EServer Server { get; } = server;

    protected string BaseUrl => Server.BaseUrl;

    public async Task InitializeAsync()
    {
        E2EDatabase.ResetPerTestState(Server.DatabasePath);
        _browser = await Server.Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is null)
        {
            return;
        }

        try
        {
            await WriteEvidenceAsync();
        }
        finally
        {
            FailureArtifacts.CurrentTest = null;
            await _browser.CloseAsync();
        }
    }

    /// <summary>A page in a context of its own, at Playwright's 1280x720 unless a viewport is given, sending <paramref name="headers"/> with every request.</summary>
    protected async Task<IPage> NewPageAsync(ViewportSize? viewport = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var context = await NewContextAsync(viewport, headers);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(DefaultTimeoutMs);
        return page;
    }

    /// <summary>
    /// A browser context of its own, for a test that opens several pages in it. It is closed with the test. <paramref name="headers"/>
    /// are sent with every request the browser makes, as a reverse proxy in front of Weir adds them.
    /// </summary>
    protected async Task<IBrowserContext> NewContextAsync(ViewportSize? viewport, IReadOnlyDictionary<string, string>? headers = null)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions { ViewportSize = viewport, ExtraHTTPHeaders = headers });
        context.Page += (_, page) => Watch(page);
        await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true });
        _contexts.Add(context);
        return context;
    }

    protected static void ApplyDefaultTimeout(IPage page) => page.SetDefaultTimeout(DefaultTimeoutMs);

    /// <summary>Notes what the browser reports about a page: its console, errors, failed requests and error responses.</summary>
    private void Watch(IPage page)
    {
        page.Console += (_, message) => Note($"console.{message.Type} {message.Text}");
        page.PageError += (_, error) => Note($"pageerror {error}");
        page.Crash += (_, _) => Note("crash");
        page.RequestFailed += (_, request) => Note($"requestfailed {request.Method} {request.Url} {request.Failure}");
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                Note($"http {response.Status} {response.Request.Method} {response.Url}");
            }
        };
        page.FrameNavigated += (_, frame) =>
        {
            if (frame.ParentFrame is null)
            {
                Note($"navigated {frame.Url}");
            }
        };
    }

    private void Note(string text)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {text}";
        lock (_browserLog)
        {
            _browserLog.Add(line);
        }
    }

    /// <summary>Writes the test's screenshots, traces, browser log and the end of the server's log; they are deleted again unless the test failed.</summary>
    private async Task WriteEvidenceAsync()
    {
        var test = FailureArtifacts.CurrentTest;
        if (test is null || _contexts.Count == 0)
        {
            return;
        }

        var folder = FailureArtifacts.FolderOf(test);
        Directory.CreateDirectory(folder);
        for (var contextIndex = 0; contextIndex < _contexts.Count; contextIndex++)
        {
            var context = _contexts[contextIndex];
            var pages = context.Pages;
            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                var screenshot = Path.Combine(folder, $"context-{contextIndex + 1}-page-{pageIndex + 1}.png");
                await Quietly(() => pages[pageIndex].ScreenshotAsync(new PageScreenshotOptions { Path = screenshot }));
            }

            var trace = Path.Combine(folder, $"context-{contextIndex + 1}-trace.zip");
            await Quietly(() => context.Tracing.StopAsync(new TracingStopOptions { Path = trace }));
        }

        string[] browserLog;
        lock (_browserLog)
        {
            browserLog = [.. _browserLog];
        }

        string[] outside = [.. Server.OutsideChanges().Select(change => $"changed outside the test: {change}")];
        await File.WriteAllLinesAsync(Path.Combine(folder, "browser.log"), [.. outside, .. browserLog]);
        var serverLog = Server.LogText().Split('\n').TakeLast(ServerLogLines);
        await File.WriteAllLinesAsync(Path.Combine(folder, "server.log"), serverLog);
    }

    /// <summary>A page or context the test already closed has nothing left to capture; that is not worth failing a test for.</summary>
    private static async Task Quietly(Func<Task> capture)
    {
        try
        {
            await capture();
        }
        catch (PlaywrightException)
        {
        }
    }
}
