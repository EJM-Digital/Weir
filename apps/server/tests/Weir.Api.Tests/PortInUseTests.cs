using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Weir.Host;
using Weir.Infrastructure.Tests;

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

        await AssertRefusedAsync(host, ((IPEndPoint)holder.LocalEndpoint).Port);
    }

    // Windows lets a bind to every address sit beside one to 127.0.0.1 alone unless the server asks for the address exclusively.
    [WindowsTheory("A wildcard bind beside a specific one is permitted by Windows only.")]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    public async Task A_server_on_every_address_refuses_a_port_another_program_holds_on_127_0_0_1_alone(string host)
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();

        await AssertRefusedAsync(host, ((IPEndPoint)holder.LocalEndpoint).Port);
    }

    [WindowsTheory("Port hijacking by a more specific bind is possible on Windows only.")]
    [InlineData("0.0.0.0", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", false)]
    [InlineData("localhost", true)]
    public async Task Nothing_can_bind_beside_a_running_server_on_127_0_0_1(string host, bool reuseAddress)
    {
        var port = FreePort();
        using var server = RealServerProcess.Start(_home, host, port);
        await AssertReadyAsync(server, "The server did not become ready");

        using var intruder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (reuseAddress)
        {
            intruder.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        var refused = Assert.Throws<SocketException>(() => intruder.Bind(new IPEndPoint(IPAddress.Loopback, port)));
        Assert.True(
            refused.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied,
            $"The bind beside the server was refused for an unexpected reason: {refused.SocketErrorCode}.");
    }

    // Exclusive use must not keep a port from a server started right after another that served requests and was stopped.
    [Fact]
    public async Task A_server_that_served_requests_and_was_stopped_can_be_started_again_on_the_same_port_at_once()
    {
        var port = FreePort();
        using (var first = RealServerProcess.Start(_home, "0.0.0.0", port))
        {
            await AssertReadyAsync(first, "The first server did not become ready");
            using var http = new HttpClient();
            for (var i = 0; i < 5; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/ready");
                request.Headers.ConnectionClose = true;
                using var response = await http.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode);
            }
        }

        using var second = RealServerProcess.Start(_home, "0.0.0.0", port);

        await AssertReadyAsync(second, "The second server did not become ready on the port the first had used");
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

    private async Task AssertRefusedAsync(string host, int port)
    {
        using var server = RealServerProcess.Start(_home, host, port);

        var exited = await server.ExitsAsync(Ceiling);

        if (!exited)
        {
            Assert.Fail($"The server did not exit on a port that is held. It printed: {await Describe(server)}");
        }

        var error = await server.ErrorAsync();
        Assert.Equal(StartRefusal.PortInUseExitCode, server.ExitCode);
        var said = $"Weir cannot start: port {port.ToString(CultureInfo.InvariantCulture)} is already in use by another program.";
        Assert.Contains(said, error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(_home, "startup-error.txt")), "A port in use is not a refusal: the tray must try again.");
        Assert.Contains($"port {port.ToString(CultureInfo.InvariantCulture)} is already in use", ReadLog(), StringComparison.Ordinal);
    }

    private static async Task AssertReadyAsync(RealServerProcess server, string what)
    {
        if (!await server.BecomesReadyAsync(Ceiling))
        {
            var kept = server.Diagnostics;
            Assert.Fail($"{what}. Kept for diagnosis: {kept}. It printed: {await Describe(server)}");
        }
    }

    // A server that is still running has written nothing complete to read; stopping it ends its output.
    private static async Task<string> Describe(RealServerProcess server)
    {
        server.Dispose();
        return await server.EverythingAsync();
    }

    private string ReadLog()
    {
        var path = Path.Join(_home, "logs", "weir.log");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
