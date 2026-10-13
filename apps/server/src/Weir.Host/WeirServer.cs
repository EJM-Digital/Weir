using System.Collections;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.Console;
using Weir.Api;
using Weir.Api.Http;
using Weir.Core;
using Weir.Core.Configuration;
using Weir.Core.Metrics;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;

namespace Weir.Host;

/// <summary>
/// Builds and starts the Weir server: configuration, logging, runtime directories, database
/// schema, then HTTP. Every startup failure surfaces as an exception with an operator message.
/// </summary>
public static class WeirServer
{
    /// <summary>The service name used when running as a Windows Service.</summary>
    public const string WindowsServiceName = "Weir";

    /// <summary>The process environment as <see cref="RuntimeEnvironment"/>.</summary>
    public static RuntimeEnvironment CurrentRuntime()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var variables = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = entry.Value as string ?? string.Empty;
        }

        return new RuntimeEnvironment(
            variables,
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.CurrentDirectory);
    }

    /// <summary>
    /// Load and validate configuration, prepare directories, logging and the database, and build
    /// the web application. <paramref name="configureBuilder"/> lets tests swap the server.
    /// </summary>
    public static WebApplication Build(
        IReadOnlyList<string> args,
        RuntimeEnvironment runtime,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = WeirOptionsLoader.Load(runtime);
        var listen = ServerListenOptions.Parse(args, runtime);
        RuntimeDirectories.Ensure(options);
        RuntimeDirectories.AssertSqliteDbLocationUsable(options.DbPath);

        // The log is opened before anything that can block, and says so, so a start that never answers still shows how far it got.
        var minimumLevel = LogLineFormat.ParseMinimumLevel(options.LogLevel);
        var logFile = new WeirLogFile(Path.Join(options.LogDir, RuntimePaths.LogFileName), TimeProvider.System);
        var logAlerts = new LogAlerts();
        var fileLogs = new WeirLogFileLoggerProvider(logFile, TimeProvider.System, minimumLevel, logAlerts);
        fileLogs.CreateLogger("Weir.Host.Startup").LogInformation(
            "Weir {Version} is starting process={ProcessId} home={Home}",
            WeirVersion.Resolve(options.VersionOverride),
            Environment.ProcessId,
            options.WeirHome);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            // Configuration comes from WEIR_* only; appsettings files and ASPNETCORE_* URLs do not apply.
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = options.Env == "production" ? Environments.Production : Environments.Development,
        });

        builder.Host.UseWindowsService(service => service.ServiceName = WindowsServiceName);
        builder.Host.UseSystemd();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            if (listen.Host is "0.0.0.0" or "*")
            {
                kestrel.ListenAnyIP(listen.Port);
            }
            else if (listen.Host is "localhost")
            {
                kestrel.ListenLocalhost(listen.Port);
            }
            else
            {
                kestrel.Listen(System.Net.IPAddress.Parse(listen.Host), listen.Port);
            }
        });

        builder.Services.AddSingleton(logFile);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(minimumLevel);
        // Kestrel and routing chatter at INFO would drown Weir's own lines, and there is no per-request access log.
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", minimumLevel);
        builder.Logging.AddConsole(console => console.FormatterName = WeirConsoleFormatter.FormatterName)
            .AddConsoleFormatter<WeirConsoleFormatter, ConsoleFormatterOptions>();
        builder.Services.AddSingleton(logAlerts);
        builder.Logging.AddProvider(fileLogs);
        var metrics = new RuntimeMetricsStore(TimeProvider.System);
        builder.Services.AddSingleton(metrics);
        builder.Logging.AddProvider(new MetricsLoggerProvider(metrics, minimumLevel));

        builder.Services.AddSingleton(listen);
        builder.Services.AddSingleton(ServerRunMode.Detect(
            UpdateFiles.DetectInstallType(options.RuntimeKind),
            WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService()));
        builder.Services.AddWeirApi(options);
        builder.Services.AddWeirJobs(options, runtime);
        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        try
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Weir.Host.Startup");
            WarnStartupMisconfigurations(options, logger);
            OpenDatabase(app, options, logger);

            var lifecycle = app.Services.GetRequiredService<ServerLifecycle>();
            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStarted.Register(() =>
            {
                lifecycle.MarkStartupComplete();
                logger.LogInformation(
                    "Weir {Version} listening on {Host}:{Port}",
                    WeirVersion.Resolve(options.VersionOverride),
                    listen.Host,
                    listen.Port);
            });
            lifetime.ApplicationStopping.Register(lifecycle.MarkStopping);
            lifetime.ApplicationStopped.Register(logFile.Dispose);
        }
        catch
        {
            logFile.Dispose();
            ((IDisposable)app).Dispose();
            throw;
        }

        app.UseWeirApi();
        return app;
    }

    /// <summary>Logs a warning for each risky or incomplete setting at startup; none of them stops the server.</summary>
    internal static void WarnStartupMisconfigurations(WeirOptions options, ILogger logger)
    {
        if (string.IsNullOrEmpty(options.SessionSecret))
        {
            logger.LogWarning(
                "WEIR_SESSION_SECRET is not set — all authentication endpoints will return HTTP 503. " +
                "Set this to a long random string before starting Weir.");
        }

        var credentials = (options.CredentialsSecret ?? string.Empty).Trim();
        if (credentials.Length is > 0 and < 32)
        {
            logger.LogWarning(
                "WEIR_CREDENTIALS_SECRET is set but shorter than 32 characters ({Length} chars). " +
                "Use a long random string to protect stored credentials.",
                credentials.Length);
        }
        else if (credentials.Length == 0 && options.Env == "production")
        {
            logger.LogWarning(
                "WEIR_CREDENTIALS_SECRET is not set — stored provider credentials will fall back to " +
                "session-secret encryption. Set a dedicated credentials secret for stronger isolation.");
        }
    }

    /// <summary>Notes this start, so System can say how often Weir restarted. A failure is logged and never stops the server.</summary>
    private static void RecordServerStart(WebApplication app, SqliteDatabase database, ILogger logger)
    {
        try
        {
            var starts = app.Services.GetRequiredService<ServerStartStore>();
            var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            var uow = UnitOfWork.OpenAsync(database).GetAwaiter().GetResult();
            try
            {
                starts.RecordStartAsync(uow, now).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            finally
            {
                uow.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (SqliteException exception)
        {
            logger.LogError(exception, "Weir startup step failed but startup will continue step={Step}", "record_server_start");
        }
    }

    private static void OpenDatabase(WebApplication app, WeirOptions options, ILogger logger)
    {
        var database = app.Services.GetRequiredService<SqliteDatabase>();
        StartupNotes.ClearStale(options.WeirHome);
        var time = app.Services.GetRequiredService<TimeProvider>();
        IDisposable? progress = null;
        var backup = new PreUpdateBackup(
            options.BackupDir,
            options.WeirHome,
            WeirVersion.Resolve(options.VersionOverride),
            time,
            logger,
            saving: sentence => progress = StartupNotes.BeginProgress(options.WeirHome, sentence, time));
        SchemaStartupOutcome outcome;
        logger.LogInformation("Opening the database path={Path}", options.DbPath);
        try
        {
            outcome = new SchemaMigrator(database).EnsureAtHead((connection, revision) => backup.Save(connection, revision));
        }
        catch (DatabaseSchemaMismatchException exception)
        {
            logger.LogCritical("Weir cannot open the database kind={Kind}: {Message}", exception.Kind, exception.Message);
            throw;
        }
        catch (PreUpdateBackupException exception)
        {
            logger.LogCritical("{Message}", exception.Message);
            StartupNotes.WriteError(options.WeirHome, PreUpdateBackupException.Headline, exception.Message);
            throw;
        }
        finally
        {
            progress?.Dispose();
        }

        if (outcome == SchemaStartupOutcome.Created)
        {
            logger.LogInformation("Created database schema revision={Revision} path={Path}", SchemaMigrator.HeadRevision, options.DbPath);
        }
        else if (outcome == SchemaStartupOutcome.Upgraded)
        {
            logger.LogInformation("Upgraded database to schema revision={Revision} path={Path}", SchemaMigrator.HeadRevision, options.DbPath);
        }
        else
        {
            logger.LogInformation("Opened database at schema revision={Revision} path={Path}", SchemaMigrator.HeadRevision, options.DbPath);
        }

        app.Services.GetRequiredService<ServerLifecycle>().MarkDatabaseOpened();
        RecordServerStart(app, database, logger);

        // The suite log's own prune (LogRetentionTask, RunAtStart) runs in the background once the periodic-task
        // lane starts, so it never holds up Kestrel from listening; pruning it again here would only prune twice (#718).

        try
        {
            var auth = app.Services.GetRequiredService<AuthService>();
            var uow = UnitOfWork.OpenAsync(database).GetAwaiter().GetResult();
            try
            {
                auth.CleanupInactiveSessionsAsync(uow).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            finally
            {
                uow.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (exception is SqliteException or FormatException or InvalidOperationException)
        {
            logger.LogError(exception, "Weir startup step failed but startup will continue step={Step}", "inactive_session_cleanup");
        }

        try
        {
            var mediaManagers = app.Services.GetRequiredService<MediaManagerConnectionStore>();
            var downloadClients = app.Services.GetRequiredService<DownloadClientConnectionStore>();
            var uow = UnitOfWork.OpenAsync(database).GetAwaiter().GetResult();
            try
            {
                // Connections are named after their kind and address; this names the ones saved with a typed name.
                mediaManagers.RefreshNamesAsync(uow).GetAwaiter().GetResult();
                downloadClients.RefreshNamesAsync(uow).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            finally
            {
                uow.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (SqliteException exception)
        {
            logger.LogError(exception, "Weir startup step failed but startup will continue step={Step}", "connection_names");
        }

        try
        {
            var setupCodes = app.Services.GetRequiredService<SetupCodeGate>();
            var auth = app.Services.GetRequiredService<AuthService>();
            var uow = UnitOfWork.OpenAsync(database).GetAwaiter().GetResult();
            bool adminExists;
            try
            {
                adminExists = !auth.BootstrapAllowedAsync(uow).GetAwaiter().GetResult();
            }
            finally
            {
                uow.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            setupCodes.EnsureStateForStartup(adminExists, logger);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Weir startup step failed but startup will continue step={Step}", "setup_code_gate");
        }
    }
}
