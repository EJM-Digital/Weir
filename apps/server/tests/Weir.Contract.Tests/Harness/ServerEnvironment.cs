namespace Weir.Contract.Tests.Harness;

/// <summary>The environment a server under test starts with: quiet by default, so a test sees only what it caused.</summary>
public static class ServerEnvironment
{
    public const string SessionSecretVariable = "WEIR_SESSION_SECRET";

    private const string DefaultSessionSecret = "contract-suite-session-secret-at-least-32-chars";

    private const string WorkerCount = "WEIR_PROCESSING_WORKER_COUNT";
    private const string Watcher = "WEIR_PROCESSING_WATCHER_ENABLED";
    private const string PeriodicEnqueue = "WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_PERIODIC_ENQUEUE_REMUX_JOBS";
    private const string MovieSweep = "WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_ENABLED";
    private const string TvSweep = "WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_ENABLED";
    public const string WorkflowSync = "WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED";

    // Settings from a developer's own shell that must not change a contract run.
    private static readonly string[] InheritedVariablesToDrop =
    [
        "WEIR_CORS_ORIGINS",
        "WEIR_TRUSTED_BROWSER_ORIGINS",
        "WEIR_ENV",
        "WEIR_LOG_LEVEL",
        "WEIR_WEB_DIST",
    ];

    private static readonly Dictionary<string, string> Defaults = new()
    {
        // The production sign-in limits are per process, and a test class signs in far more than ten times.
        // The limits themselves are covered by their own tests, which lower these.
        ["WEIR_BOOTSTRAP_RATE_MAX_ATTEMPTS"] = "10000",
        ["WEIR_AUTH_LOGIN_RATE_MAX_ATTEMPTS"] = "10000",

        // No in-process workers, no file-system watcher, and periodic scans that queue no files; scenarios
        // that need processing turn the workers on. Periodic scan jobs are still queued for every enabled
        // library with a watched folder, so a test counts only the jobs it caused (#533).
        [WorkerCount] = "0",
        [Watcher] = "0",
        [PeriodicEnqueue] = "0",
        [MovieSweep] = "0",
        [TvSweep] = "0",

        // Connecting a manager does not rewrite workflows behind a test's back; the sync's own tests turn it on.
        [WorkflowSync] = "0",

        // No server reaches the real metadata service; posters stay off unless a test points at a fake.
        ["WEIR_ARTWORK_GATEWAY_URL"] = "off",
    };

    /// <summary>
    /// The settings an operator runs with, which the quiet defaults switch off: the in-process workers (ten, as installed), the
    /// file-system watcher, periodic scans that queue files, and the work-file sweeps. For a test of the whole product, such as
    /// the browser tests, that needs the log to hold what a real install's would.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OperatorDefaults { get; } = new Dictionary<string, string>
    {
        [WorkerCount] = "10",
        [Watcher] = "1",
        [PeriodicEnqueue] = "1",
        [MovieSweep] = "1",
        [TvSweep] = "1",
        [WorkflowSync] = "1",
    };

    /// <summary>Sets <paramref name="environment"/> up for a server with this data folder and address.</summary>
    public static void Apply(
        IDictionary<string, string?> environment,
        string home,
        Uri baseUrl,
        IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var variable in InheritedVariablesToDrop)
        {
            environment.Remove(variable);
        }

        environment["WEIR_HOME"] = home;
        environment[SessionSecretVariable] = Environment.GetEnvironmentVariable(SessionSecretVariable) is { Length: > 0 } secret
            ? secret
            : DefaultSessionSecret;
        environment["ASPNETCORE_URLS"] = baseUrl.GetLeftPart(UriPartial.Authority);
        foreach (var (name, value) in Defaults.Concat(overrides))
        {
            environment[name] = value;
        }
    }
}
