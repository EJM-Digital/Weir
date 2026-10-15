using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Weir.Host;

namespace Weir.Api.Tests;

/// <summary>
/// The real server, started on a port another program already holds. It must end the start with a plain sentence that names the port and an
/// exit code that says "try again" (the tray retries it: another program may let go of the port), and it must not leave a
/// <c>startup-error.txt</c>, which tells the tray that starting again cannot help.
/// </summary>
public sealed class PortInUseTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    private readonly string _home = Path.Join(Path.GetTempPath(), "weir-port-tests-" + Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", false)]
    [InlineData("0.0.0.0", true)]
    public async Task A_server_started_on_a_port_another_program_holds_says_so_and_exits_with_the_code_that_asks_to_be_tried_again(string host, bool heldOnEveryAddress)
    {
        using var holder = heldOnEveryAddress ? new TcpListener(IPAddress.IPv6Any, 0) { Server = { DualMode = true } } : new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;

        var run = await RunServerAsync(host, port);

        Assert.True(run.Exited, $"The server did not exit on a port that is held. It printed: {run.Error}");
        Assert.Equal(StartRefusal.PortInUseExitCode, run.ExitCode);
        var said = $"Weir cannot start: port {port.ToString(CultureInfo.InvariantCulture)} is already in use by another program.";
        Assert.Contains(said, run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(_home, "startup-error.txt")), "A port in use is not a refusal: the tray must try again.");
        Assert.Contains($"port {port.ToString(CultureInfo.InvariantCulture)} is already in use", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_listener_refused_for_a_port_in_use_is_taken_for_one()
    {
        var inUse = new SocketException((int)SocketError.AddressAlreadyInUse);
        var denied = new SocketException((int)SocketError.AccessDenied);

        Assert.True(PortInUse.Caused(new IOException("Failed to bind to address.", new InvalidOperationException("in use", inUse))));
        Assert.False(PortInUse.Caused(new IOException("Failed to bind to address.", new InvalidOperationException("denied", denied))));
        Assert.False(PortInUse.Caused(new IOException("The disk is full.")));
        Assert.False(PortInUse.Caused(new InvalidOperationException("in use", inUse)));
    }

    private async Task<ServerRun> RunServerAsync(string host, int port)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Weir.dll"));
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        start.Environment["WEIR_HOME"] = _home;
        start.Environment["WEIR_ENV"] = "production";
        start.Environment["WEIR_SESSION_SECRET"] = new string('s', 48);
        start.Environment["WEIR_CREDENTIALS_SECRET"] = new string('c', 48);
        start.Environment["WEIR_ARTWORK_GATEWAY_URL"] = "off";
        start.Environment["WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED"] = "0";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The server could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancelled = new CancellationTokenSource(Ceiling);
        try
        {
            await process.WaitForExitAsync(cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return new ServerRun(false, process.ExitCode, await output + await error);
        }

        return new ServerRun(true, process.ExitCode, await error);
    }

    private string ReadLog()
    {
        var path = Path.Join(_home, "logs", "weir.log");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private sealed record ServerRun(bool Exited, int ExitCode, string Error);
}
