using System.Reflection;
using System.Runtime.Versioning;

namespace Weir.Api.Tests;

/// <summary>
/// A reproduction, not a regression test: it asserts the hang it demonstrates, so it runs only when asked
/// (<c>WEIR_COVERLET_REPRO=1</c>, under <c>--collect:"XPlat Code Coverage"</c>).
/// <para>
/// Coverlet's collector instruments Weir's assemblies in place, so a server these tests start loads the instrumented copies. Each
/// instrumented assembly flushes its hit counts when the process exits (<c>ModuleTrackerTemplate.UnloadModule</c>), under a named mutex
/// that is waited for with no timeout and that has no more than the file name's first segment in its name (<c>Weir_Mutex</c> for
/// every <c>Weir.*</c> assembly, in every process of every run on the machine). A process killed while it holds the mutex abandons it,
/// and the next process to flush gets <see cref="AbandonedMutexException"/> out of its exit handler. A server whose standard error is a
/// full pipe nobody reads then blocks writing the unhandled exception and never exits, which is what
/// <see cref="ServerOutputPipeTests"/> saw in CI.
/// </para>
/// </summary>
public sealed class CoverletExitMutexReproTests : IDisposable
{
    private readonly string _home = Path.Join(Path.GetTempPath(), "weir-coverlet-repro-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The server may still hold a file for a moment; the temp folder is cleared with the rest.
        }
    }

    [ReproFact]
    [SupportedOSPlatform("windows")]
    public async Task A_server_with_a_full_error_pipe_never_exits_once_a_killed_process_has_abandoned_coverlets_exit_mutex()
    {
        var hitsFile = typeof(Weir.Core.WeirVersion).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .FirstOrDefault(field => field.Name == "HitsFilePath")
            ?? throw new InvalidOperationException("Weir.Core is not instrumented: run this under --collect:\"XPlat Code Coverage\".");
        var name = Path.GetFileNameWithoutExtension((string)hitsFile.GetValue(null)!) + "_Mutex";

        // What a process killed in the middle of its flush leaves behind: a mutex owned by a thread that ended.
        var owner = new Thread(() => _ = new Mutex(initiallyOwned: true, name));
        owner.Start();
        owner.Join();
        using var keepsItAlive = new Mutex(initiallyOwned: false, name);

        var environment = new Dictionary<string, string>
        {
            ["WEIR_HOME"] = _home,
            ["WEIR_ENV"] = "production",
            ["WEIR_SESSION_SECRET"] = new string('s', 48),
            ["WEIR_CREDENTIALS_SECRET"] = new string('c', 48),
            ["WEIR_ARTWORK_GATEWAY_URL"] = "off",
            ["WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED"] = "0",
            ["WEIR_FILE_MODE_OUTPUT"] = "not-a-mode",
        };
        using var server = UnreadStdoutServer.Start(_home, 59999, environment);

        var exited = await server.ExitsAsync(TimeSpan.FromSeconds(30));

        Assert.False(exited, "The server refused its settings and left; the abandoned mutex did not hold it.");
    }

    private sealed class ReproFactAttribute : FactAttribute
    {
        public ReproFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("WEIR_COVERLET_REPRO") != "1")
            {
                Skip = "A reproduction of a hang under coverage: set WEIR_COVERLET_REPRO=1 and collect coverage to run it.";
            }
        }
    }
}
