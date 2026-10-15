using Weir.Core.Artwork;

namespace Weir.Core.Configuration;

/// <summary>How the session cookie's <c>Secure</c> flag is decided (<c>WEIR_SESSION_COOKIE_SECURE</c>).</summary>
public enum CookieSecureMode
{
    /// <summary>HTTPS-only exactly when the request arrived over HTTPS.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>The session cookie's <c>SameSite</c> value (<c>WEIR_SESSION_COOKIE_SAMESITE</c>).</summary>
public enum CookieSameSite
{
    Lax,
    Strict,
    None,
}

/// <summary>
/// Runtime configuration loaded once at process start from the <c>WEIR_*</c> environment variables.
/// Build it with <see cref="WeirOptionsLoader"/>.
/// </summary>
/// <remarks>
/// Integer settings without a natural upper bound are <see cref="long"/>; inputs outside the
/// <see cref="long"/> range saturate instead of failing.
/// </remarks>
public sealed record WeirOptions
{
    public required string Env { get; init; }
    public required string LogLevel { get; init; }
    public required IReadOnlyList<string> CorsOrigins { get; init; }
    public required string? SessionSecret { get; init; }
    public required string? CredentialsSecret { get; init; }
    public required IReadOnlyList<string> PreviousCredentialsSecrets { get; init; }
    public required string SessionCookieName { get; init; }
    public required CookieSecureMode SessionCookieSecureMode { get; init; }
    public required CookieSameSite SessionCookieSameSite { get; init; }
    public required long SessionIdleMinutes { get; init; }
    public required long SessionAbsoluteDays { get; init; }
    public required long SessionTrustedIdleMinutes { get; init; }
    public required long SessionTrustedAbsoluteDays { get; init; }
    public required IReadOnlyList<string> TrustedBrowserOriginsOverride { get; init; }
    public required IReadOnlyList<string> TrustedProxyIps { get; init; }

    /// <summary>
    /// Extra Host header values the allow-list middleware accepts beyond IP literals, <c>localhost</c>,
    /// single-label and local-network names, and the CORS/trusted-browser origins (<c>WEIR_ALLOWED_HOSTS</c>,
    /// comma-separated). A reverse-proxy domain belongs here. A leading <c>*.</c> also allows its subdomains.
    /// </summary>
    public required IReadOnlyList<string> AllowedHosts { get; init; }
    public required long AuthLoginRateMaxAttempts { get; init; }
    public required long AuthLoginRateWindowSeconds { get; init; }
    public required long BootstrapRateMaxAttempts { get; init; }
    public required long BootstrapRateWindowSeconds { get; init; }
    public required bool SecurityEnableHsts { get; init; }
    public required string? MetricsBearerToken { get; init; }

    /// <summary>
    /// Guards the hand-off intake webhook for installs with no per-connection secret yet.
    /// Read from <c>WEIR_MEDIA_MANAGER_WEBHOOK_SECRET</c> only.
    /// </summary>
    public required string? MediaManagerWebhookSecret { get; init; }

    public required string WeirHome { get; init; }
    public required string DbPath { get; init; }
    public required string BackupDir { get; init; }
    public required string LogDir { get; init; }
    public required string TempDir { get; init; }

    /// <summary>
    /// 0 = no in-process Processing workers; otherwise 1 .. <see cref="Weir.Core.Processing.OperatorSettingsRules.MaxFilesAtOnce"/> worker slots.
    /// </summary>
    public required int ProcessingWorkerCount { get; init; }

    /// <summary>
    /// How long a worker's claim on a <c>jobs</c> row lasts before another worker may reclaim
    /// it (<c>WEIR_PROCESSING_JOB_LEASE_SECONDS</c>, #540). A heartbeat renews it roughly every
    /// third of this while a handler runs, so a job taking longer than this is still never claimed
    /// twice; this only bounds how long a crashed worker's row sits unclaimed before recovery.
    /// </summary>
    public required int ProcessingJobLeaseSeconds { get; init; }
    public required bool ProcessingWatcherEnabled { get; init; }

    /// <summary>
    /// How long a write waits for the database before it gives up with a 503 and a <c>Retry-After</c> (<c>WEIR_SQLITE_BUSY_TIMEOUT_SECONDS</c>,
    /// 1 to 120, default 30). A writer queues for Weir's own write gate and then for a writer outside Weir, such as a backup tool, for
    /// this long in all. Raising it only makes a media manager wait longer for the same answer.
    /// </summary>
    public int SqliteBusyTimeoutSeconds { get; init; } = DefaultSqliteBusyTimeoutSeconds;

    /// <summary>The write wait when <c>WEIR_SQLITE_BUSY_TIMEOUT_SECONDS</c> is unset.</summary>
    public const int DefaultSqliteBusyTimeoutSeconds = 30;

    /// <summary>
    /// For tests only (<c>WEIR_LIBRARY_SWAP_HOLD_AT</c>): the name of the point in a library swap where it stops for good so a test can
    /// end the process there (<c>HeldSwapProgress</c>). Unset on a real install: the loader refuses it when <c>WEIR_ENV</c> is production.
    /// </summary>
    public string? LibrarySwapHoldAt { get; init; }

    /// <summary>
    /// For tests only (<c>WEIR_PERIODIC_TASK_SPEEDUP</c>): how many times shorter every periodic task's interval is, so a browser test
    /// waits for the server's next look at a folder or a pause that has run out in a second or two instead of half a minute. 1 on a real
    /// install: the loader refuses any other value when <c>WEIR_ENV</c> is production.
    /// </summary>
    public int PeriodicTaskSpeedUp { get; init; } = 1;

    /// <summary>
    /// Whether Weir sets up its workflows from a media manager that reports its folders (Deluno) and keeps them in step
    /// (<c>WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED</c>, default on). Off leaves every workflow's folders to be typed.
    /// </summary>
    public bool ManagerWorkflowSyncEnabled { get; init; } = true;
    public required double ProcessingWatcherDebounceSeconds { get; init; }
    /// <summary>
    /// #533: the global kill switch for the periodic watched-folder scan timer itself
    /// (<c>WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_SCHEDULE_ENABLED</c>, default on). Distinct
    /// from <see cref="ProcessingWatchedFolderRemuxScanDispatchPeriodicEnqueueRemuxJobs"/>, which only
    /// controls whether a periodic scan that *does* run may queue remux work off what it finds — with
    /// this off, no periodic scan runs at all for any scope, regardless of that other setting or of a
    /// library's own <c>schedule_enabled</c>/<c>enabled</c> columns. A manual scan (the HTTP enqueue
    /// route) is unaffected: it is a one-off request, not the timer this switch stops.
    /// </summary>
    public required bool ProcessingWatchedFolderRemuxScanDispatchScheduleEnabled { get; init; }

    public required bool ProcessingWatchedFolderRemuxScanDispatchPeriodicEnqueueRemuxJobs { get; init; }
    public required int ProcessingProbeSizeMb { get; init; }
    public required int ProcessingAnalyzeDurationSeconds { get; init; }
    public required int ProcessingMovieOutputCleanupMinAgeSeconds { get; init; }
    public required int ProcessingTvOutputCleanupMinAgeSeconds { get; init; }
    public required bool ProcessingWorkTempStaleSweepMovieScheduleEnabled { get; init; }
    public required int ProcessingWorkTempStaleSweepMovieScheduleIntervalSeconds { get; init; }
    public required bool ProcessingWorkTempStaleSweepTvScheduleEnabled { get; init; }
    public required int ProcessingWorkTempStaleSweepTvScheduleIntervalSeconds { get; init; }
    public required int ProcessingWorkTempStaleSweepMinStaleAgeSeconds { get; init; }

    public required int JobRowsRetentionDays { get; init; }
    public required int JobRowsRetentionScheduleIntervalSeconds { get; init; }
    public required string? ArrRadarrBaseUrl { get; init; }
    public required string? ArrRadarrApiKey { get; init; }
    public required string? ArrSonarrBaseUrl { get; init; }
    public required string? ArrSonarrApiKey { get; init; }

    /// <summary>
    /// The built web app to serve (<c>WEIR_WEB_DIST</c>), resolved to an absolute path, or
    /// <see langword="null"/> when unset. Whether it holds an <c>index.html</c> is checked per request.
    /// </summary>
    public required string? WebDist { get; init; }

    /// <summary><c>WEIR_VERSION</c> when set; otherwise the build's own version is reported.</summary>
    public required string? VersionOverride { get; init; }

    /// <summary><c>WEIR_RUNTIME</c> (<c>windows</c>, <c>docker</c> or <c>source</c>), which the update check reports as the install type.</summary>
    public string? RuntimeKind { get; init; }

    /// <summary>
    /// #555: whether Weir should <c>chown</c> a file it just published, or a folder it just created for one, to
    /// <see cref="OutputOwnershipUid"/>/<see cref="OutputOwnershipGid"/> (<c>WEIR_CHOWN_OUTPUT</c>, default off).
    /// Linux only; ignored on Windows (logged once at startup when set — there is no POSIX owner there).
    /// </summary>
    public required bool OutputOwnershipChownEnabled { get; init; }

    /// <summary>
    /// The target owner for <see cref="OutputOwnershipChownEnabled"/>: the first non-blank of
    /// <c>WEIR_PUID</c>/<c>PUID</c> (default 1000) — the same variable the Docker entrypoint uses to remap the
    /// container's own runtime user, so a chowned output file matches the identity Weir itself runs as.
    /// </summary>
    public required uint OutputOwnershipUid { get; init; }

    /// <summary>The target group for <see cref="OutputOwnershipChownEnabled"/>: first non-blank of <c>WEIR_PGID</c>/<c>PGID</c> (default 1000).</summary>
    public required uint OutputOwnershipGid { get; init; }

    /// <summary>
    /// Optional file mode applied to a file Weir just published (<c>WEIR_FILE_MODE_OUTPUT</c>, an octal string such
    /// as <c>664</c>). <see langword="null"/> when unset. Linux only.
    /// </summary>
    public required UnixFileMode? OutputOwnershipFileMode { get; init; }

    /// <summary>
    /// Optional directory mode applied to a folder Weir just created to publish into (<c>WEIR_DIR_MODE_OUTPUT</c>,
    /// an octal string such as <c>775</c> or the setgid form <c>2775</c>). <see langword="null"/> when unset. Linux only.
    /// </summary>
    public required UnixFileMode? OutputOwnershipDirectoryMode { get; init; }

    /// <summary>
    /// Where poster and original-language lookups are asked (<c>WEIR_ARTWORK_GATEWAY_URL</c>): Deluno's metadata service unless set. Empty means
    /// those lookups are switched off for this process (the variable is <c>off</c>).
    /// </summary>
    public string ArtworkGatewayUrl { get; init; } = ArtworkPosterSource.DefaultGatewayUrl;

    /// <summary>Origins allowed for the unsafe-request Origin/Referer check.</summary>
    public IReadOnlyList<string> TrustedBrowserOrigins =>
        TrustedBrowserOriginsOverride.Count > 0 ? TrustedBrowserOriginsOverride : CorsOrigins;
}
