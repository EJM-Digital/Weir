using System.Net;
using System.Net.Sockets;

namespace Weir.Api.Tests;

/// <summary>A child server that is still running at its ceiling leaves a dump and its threads behind before the test stops it.</summary>
public sealed class ChildDiagnosticsTests : IDisposable
{
    private readonly string _home = Path.Join(Path.GetTempPath(), "weir-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder = Path.Join(Path.GetTempPath(), "weir-diagnostics-out-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var path in new[] { _home, _folder })
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The temp folder is cleared with the rest.
            }
        }
    }

    [Fact]
    public async Task A_server_still_running_at_its_ceiling_leaves_its_threads_and_a_dump_in_the_diagnostics_folder()
    {
        var previous = Environment.GetEnvironmentVariable(ChildDiagnostics.FolderVariable);
        Environment.SetEnvironmentVariable(ChildDiagnostics.FolderVariable, _folder);
        try
        {
            using var server = RealServerProcess.Start(_home, "127.0.0.1", FreePort());
            Assert.True(await server.BecomesReadyAsync(TimeSpan.FromSeconds(60)));

            var exited = await server.ExitsAsync(TimeSpan.FromMilliseconds(200));

            Assert.False(exited);
            Assert.StartsWith(_folder, server.Diagnostics, StringComparison.Ordinal);
            var report = await File.ReadAllTextAsync(server.Diagnostics);
            Assert.Contains("had not finished at its ceiling", report, StringComparison.Ordinal);
            Assert.True(report.Contains("os thread", StringComparison.Ordinal), report);
            var dump = Assert.Single(Directory.GetFiles(_folder, "*.dmp"));
            Assert.True(new FileInfo(dump).Length > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChildDiagnostics.FolderVariable, previous);
        }
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
