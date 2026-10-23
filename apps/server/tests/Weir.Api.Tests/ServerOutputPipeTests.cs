using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Weir.Infrastructure.Tests;

namespace Weir.Api.Tests;

/// <summary>
/// The real server, with its standard output and error set to a pipe that is full and that nobody reads. It must start and log, and
/// when it refuses to start it must say why and exit, whatever the pipe is doing: a console that waits for ever would otherwise keep
/// the server from logging, listening, creating the event the tray stops it by and exiting.
/// </summary>
public sealed class ServerOutputPipeTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    private readonly string _home = Path.Join(Path.GetTempPath(), "weir-pipe-tests-" + Guid.NewGuid().ToString("N"));

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

    [WindowsFact("A pipe that is full is made and handed to a child with CreateProcess.")]
    [SupportedOSPlatform("windows")]
    public async Task A_server_whose_standard_output_is_a_full_pipe_nobody_reads_still_starts_and_logs()
    {
        var port = FreePort();
        using var server = UnreadStdoutServer.Start(_home, port, ServerEnvironment());

        var ready = await server.BecomesReadyAsync(Ceiling);

        var log = Path.Join(_home, "logs", "weir.log");
        var lines = ReadLog(log);
        Assert.True(ready, $"The server did not become ready with its standard output full (exit code {server.ExitCode}); its log has {lines.Length} lines. Kept for diagnosis: {server.Diagnostics}");
        Assert.Contains("listening on", string.Join(Environment.NewLine, lines), StringComparison.Ordinal);
    }

    [WindowsFact("A pipe that is full is made and handed to a child with CreateProcess.")]
    [SupportedOSPlatform("windows")]
    public async Task A_server_that_refuses_its_settings_with_standard_error_a_full_pipe_still_exits_and_leaves_the_reason_for_the_tray()
    {
        var environment = ServerEnvironment();
        environment["WEIR_FILE_MODE_OUTPUT"] = "not-a-mode";
        using var server = UnreadStdoutServer.Start(_home, FreePort(), environment);

        var exited = await server.ExitsAsync(Ceiling);

        Assert.True(exited, $"The server did not exit after refusing its settings, with standard error a full pipe. Kept for diagnosis: {server.Diagnostics}");
        Assert.Equal(1, server.ExitCode);
        var note = File.ReadAllText(Path.Join(_home, "startup-error.txt")).Split('\n', 2);
        Assert.Equal("Weir's settings are not valid", note[0]);
        Assert.Contains("WEIR_FILE_MODE_OUTPUT", note[1], StringComparison.Ordinal);
    }

    private Dictionary<string, string> ServerEnvironment() => new()
    {
        ["WEIR_HOME"] = _home,
        ["WEIR_ENV"] = "production",
        ["WEIR_SESSION_SECRET"] = new string('s', 48),
        ["WEIR_CREDENTIALS_SECRET"] = new string('c', 48),
        ["WEIR_ARTWORK_GATEWAY_URL"] = "off",
        ["WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED"] = "0",
    };

    // The server keeps the log open while it runs.
    private static string[] ReadLog(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
