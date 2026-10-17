using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.E2E.Tests.Harness;

/// <summary>
/// The one server and the one Playwright driver every browser test shares (<c>ICollectionFixture</c>), for the
/// whole run. The server is the real built host serving the built web
/// app itself, exactly as the Docker and Windows packages do. Without <c>WEIR_E2E=1</c> nothing starts.
/// </summary>
public sealed class E2EServer : IAsyncLifetime
{
    private const string NoRouteToTheInternet = "http://127.0.0.1:9";

    private WeirServer? _server;
    private IPlaywright? _playwright;
    private DateTime _webAppBuiltAt;

    public IPlaywright Playwright => _playwright ?? throw NotStarted();

    public string DatabasePath => (_server ?? throw NotStarted()).DatabasePath;

    public string Home => (_server ?? throw NotStarted()).Home;

    /// <summary>
    /// What changed under the run that no test did, for a failed test's evidence: the server is served the built web app from
    /// disk, so a rebuild of it (another `npm run build`) swaps its files under a page that is loading them, and a server that has
    /// stopped answers nothing.
    /// </summary>
    public IReadOnlyList<string> OutsideChanges()
    {
        var changes = new List<string>();
        var server = _server ?? throw NotStarted();
        if (!server.IsRunning)
        {
            changes.Add("The server process is no longer running.");
        }

        if (File.GetLastWriteTimeUtc(WebAppIndex) != _webAppBuiltAt)
        {
            changes.Add($"The built web app ({WebAppIndex}) was replaced after the run started.");
        }

        return changes;
    }

    /// <summary>Everything the server has written to its output so far.</summary>
    public string LogText() => (_server ?? throw NotStarted()).LogText();

    /// <summary>The server's address without a trailing slash.</summary>
    public string BaseUrl => (_server ?? throw NotStarted()).BaseUrl.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// A server of its own, set up as the Windows package is (the About screen offers an update to install only there), for a
    /// test that has to stop and start it: the shared server serves every other test and is left running. Its own look at the
    /// releases goes through a proxy that refuses every connection, so what the tray says (the test, through the tray's files) is
    /// all the About screen has to go on, whatever Weir has been released since.
    /// </summary>
    public static Task<WeirServer> StartWindowsInstallAsync() =>
        WeirServer.StartNewAsync(Environment.With(
            ("WEIR_RUNTIME", "windows"),
            ("HTTPS_PROXY", NoRouteToTheInternet),
            ("HTTP_PROXY", NoRouteToTheInternet),
            ("ALL_PROXY", NoRouteToTheInternet),
            ("NO_PROXY", string.Empty)));

    /// <summary>
    /// A server of its own whose scheduled tasks (the look at the workflows' folders every 15 s, the lifting of a pause that has run out
    /// every 30 s) run <paramref name="times"/> times as often, for a test that waits for one of them: it is the server's own test seam
    /// (<c>WEIR_PERIODIC_TASK_SPEEDUP</c>, refused when <c>WEIR_ENV</c> is production), so the shared server keeps the pace a real install has.
    /// </summary>
    public static Task<WeirServer> StartWithQuickTasksAsync(int times) =>
        WeirServer.StartNewAsync(Environment.With(
            ("WEIR_ENV", "development"),
            ("WEIR_PERIODIC_TASK_SPEEDUP", times.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    /// <summary>
    /// A server of its own with these settings added, for a test that needs what the shared server does not have: a reverse proxy in front of
    /// it, or sign-in limits low enough to reach.
    /// </summary>
    public static Task<WeirServer> StartWithSettingsAsync(params (string Name, string Value)[] settings) =>
        WeirServer.StartNewAsync(Environment.With(settings));

    /// <summary>
    /// A server of its own that probes files with the fake tools, for a test whose files must take as long to look at as it
    /// scripts: the shared server probes with whatever ffprobe the machine has, which answers as fast as the machine does.
    /// </summary>
    public static Task<WeirServer> StartWithFakeToolsAsync(FakeFfmpeg tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return WeirServer.StartNewAsync(Environment.With(tools.Env));
    }

    public async Task InitializeAsync()
    {
        if (!E2EFactAttribute.IsEnabled)
        {
            return;
        }

        if (!File.Exists(WebAppIndex))
        {
            throw new InvalidOperationException("Weir E2E needs the built web app: run `npm ci && npm run build` in apps/web first.");
        }

        _webAppBuiltAt = File.GetLastWriteTimeUtc(WebAppIndex);
        _server = await WeirServer.StartNewAsync(Environment);
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        _playwright?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    // The server runs as an operator's does: the workers, the file watcher, the periodic scan and the work file
    // sweeps are on (without the sweeps the log has no job rows for the Jobs source to show).
    private static Dictionary<string, string> Environment =>
        ServerEnvironment.OperatorDefaults.With(("WEIR_WEB_DIST", RepoPaths.WebDist));

    private static string WebAppIndex => Path.Combine(RepoPaths.WebDist, "index.html");

    private static InvalidOperationException NotStarted() => new("The E2E server has not started: set WEIR_E2E=1.");
}
