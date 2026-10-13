using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Weir.Infrastructure.Tests;

namespace Weir.Api.Tests;

/// <summary>
/// A server whose standard output is a pipe that is full and never read must still start. It once did not: its first log call
/// wrote to that pipe and waited, before anything reached weir.log, so it never listened, never created its stop event, never
/// touched the database and never exited, which is what the Windows tray saw after an update (no answer on /ready for 60 s).
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
        using var server = UnreadStdoutServer.Start(
            _home,
            port,
            new Dictionary<string, string>
            {
                ["WEIR_HOME"] = _home,
                ["WEIR_ENV"] = "production",
                ["WEIR_SESSION_SECRET"] = new string('s', 48),
                ["WEIR_CREDENTIALS_SECRET"] = new string('c', 48),
                ["WEIR_ARTWORK_GATEWAY_URL"] = "off",
                ["WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED"] = "0",
            });

        var ready = await server.BecomesReadyAsync(Ceiling);

        var log = Path.Join(_home, "logs", "weir.log");
        var lines = ReadLog(log);
        Assert.True(ready, $"The server did not become ready with its standard output full (exit code {server.ExitCode}); its log has {lines.Length} lines.");
        Assert.Contains("listening on", string.Join(Environment.NewLine, lines), StringComparison.Ordinal);
    }

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
