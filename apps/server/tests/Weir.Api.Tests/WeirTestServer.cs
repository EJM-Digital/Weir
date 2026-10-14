using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Configuration;
using Weir.Host;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests;

namespace Weir.Api.Tests;

/// <summary>The real server, built by <see cref="WeirServer"/>, on an in-memory test server with its own WEIR_HOME.</summary>
internal sealed class WeirTestServer : IAsyncDisposable
{
    /// <summary>Off unless a test is about it, so connecting a manager never changes a workflow behind another test's back.</summary>
    public const string ManagerWorkflowSyncVariable = "WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED";

    private readonly WebApplication _app;

    private WeirTestServer(string home, WebApplication app)
    {
        Home = home;
        _app = app;
        Client = app.GetTestClient();
    }

    public string Home { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public TestServer TestServer => _app.GetTestServer();

    /// <summary>Keep the home directory on dispose (for a second server on the same home).</summary>
    public bool KeepHome { get; set; }

    /// <summary>
    /// Starts a server on its own home. A database that is already migrated is copied in first, which is faster and still
    /// goes through the server's schema check; <paramref name="prepareHome"/> or an earlier server on the same home may
    /// leave one of its own, and <paramref name="freshDatabase"/> starts on an empty folder so the server creates and
    /// migrates the database itself.
    /// </summary>
    public static async Task<WeirTestServer> StartAsync(
        IEnumerable<(string Name, string Value)>? variables = null,
        bool signedIn = false,
        Action<string>? prepareHome = null,
        string? home = null,
        Action<IServiceCollection>? configureServices = null,
        bool freshDatabase = false)
    {
        home ??= Path.Join(Path.GetTempPath(), "weir-api-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        prepareHome?.Invoke(home);
        var databasePath = Path.Join(home, "data", "weir.sqlite3");
        if (!freshDatabase && !File.Exists(databasePath))
        {
            MigratedDatabaseTemplate.CopyTo(databasePath);
        }

        // No test reaches the real metadata service: posters are off here unless a test names a stand-in gateway.
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal) { ["WEIR_HOME"] = home, ["WEIR_ARTWORK_GATEWAY_URL"] = "off", [ManagerWorkflowSyncVariable] = "0" };
        foreach (var (name, value) in variables ?? [])
        {
            dictionary[name] = value.Replace("{home}", home, StringComparison.Ordinal);
        }

        var runtime = new RuntimeEnvironment(dictionary, OperatingSystem.IsWindows(), home, home);
        var app = WeirServer.Build([], runtime, builder =>
        {
            builder.WebHost.UseTestServer();
            // TestServer leaves Connection.RemoteIpAddress unset; the tray and every real browser reach
            // Weir over loopback, so default to that here too. A test that needs a different peer sets one
            // via TestServer.SendAsync before the pipeline runs, which this does not override.
            builder.Services.AddSingleton<IStartupFilter, LoopbackConnectionStartupFilter>();
            if (signedIn)
            {
                builder.Services.AddSingleton<IOperatorAuthentication, SignedInAuthentication>();
            }

            configureServices?.Invoke(builder.Services);
        });
        await app.StartAsync();
        return new WeirTestServer(home, app);
    }

    /// <summary>
    /// Waits for start-up recovery to finish. Kestrel listens before it does, and readiness is not ready until it has,
    /// so a test that reads readiness once, rather than polling it, waits here first.
    /// </summary>
    public Task WaitForStartupRecoveryAsync() =>
        Services.GetRequiredService<JobsStartupRecoveryService>().RecoveryCompleted.WaitAsync(StartupRecoveryCeiling);

    /// <summary>Create a built-looking web app under <paramref name="home"/>/web.</summary>
    public static void WriteWebDist(string home)
    {
        var dist = Path.Join(home, "web");
        Directory.CreateDirectory(Path.Join(dist, "assets"));
        File.WriteAllText(Path.Join(dist, "index.html"), "<!doctype html><title>Weir</title>");
        File.WriteAllText(Path.Join(dist, "favicon.txt"), "icon");
        File.WriteAllText(Path.Join(dist, "assets", "app-abc123.js"), "console.log('plain');");
        File.WriteAllText(Path.Join(dist, "assets", "app-abc123.js.br"), "brotli-bytes");
        File.WriteAllText(Path.Join(dist, "assets", "app-abc123.js.gz"), "gzip-bytes");
        File.WriteAllText(Path.Join(dist, "assets", "font.woff2"), "font");
    }

    private static readonly TimeSpan StartupRecoveryCeiling = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan BreakDatabaseDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Replace the database file with a directory, so every new connection fails.
    /// </summary>
    /// <remarks>
    /// <see cref="SqliteConnection.ClearAllPools"/> closes every pooled native handle in the process, not only
    /// this database's own pool (<see cref="SqliteDatabase.ClearPool"/>), which is what a background timer (the
    /// manager heartbeat, library mode's schedule) holding the file open needs to actually let go of it — but a
    /// timer can still open a fresh connection at any moment after that clear, including between the delete and
    /// the directory create below, and on Windows the delete fails for as long as that connection is in use. So
    /// each attempt starts with its own clear and tries again at once; the deadline only bounds how long a
    /// loaded runner may take to finish the timer's brief query, it is not a wait for anything to happen.
    /// </remarks>
    public async Task BreakDatabaseAsync()
    {
        var dbPath = Path.Join(Home, "data", "weir.sqlite3");
        var database = Services.GetRequiredService<SqliteDatabase>();
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            database.ClearPool();
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(dbPath);
                File.Delete(dbPath + "-wal");
                File.Delete(dbPath + "-shm");
                Directory.CreateDirectory(dbPath);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && deadline.Elapsed < BreakDatabaseDeadline)
            {
                await Task.Delay(10);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        var database = Services.GetRequiredService<SqliteDatabase>();
        await _app.StopAsync();
        await _app.DisposeAsync();
        database.ClearPool();
        if (KeepHome)
        {
            return;
        }

        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing on Windows; the temp folder is cleaned up eventually.
        }
    }

    private sealed class SignedInAuthentication : IOperatorAuthentication
    {
        public ValueTask<OperatorAuthenticationResult> AuthenticateAsync(HttpContext context) =>
            ValueTask.FromResult(OperatorAuthenticationResult.SignedIn);
    }

    private sealed class LoopbackConnectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
