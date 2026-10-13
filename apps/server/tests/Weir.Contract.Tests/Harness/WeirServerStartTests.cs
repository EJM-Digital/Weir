using System.Net;
using System.Net.Sockets;

namespace Weir.Contract.Tests.Harness;

/// <summary>Checks how the harness starts a server when the port it picked is taken, and when the server refuses to start for another reason.</summary>
[ContractArea("harness")]
public sealed class WeirServerStartTests
{
    private const string RefusedSetting = "WEIR_ARTWORK_GATEWAY_URL";

    [Fact]
    public async Task A_port_taken_before_the_server_binds_is_replaced_by_a_new_one()
    {
        using var occupied = Occupy();
        var picks = new Queue<int>([occupied.Port]);

        await using var server = await WeirServer.StartWithPortsAsync(null, () => picks.Count > 0 ? picks.Dequeue() : WeirServer.FreePort());

        Assert.NotEqual(occupied.Port, server.BaseUrl.Port);
        Assert.Contains($"port {occupied.Port} was taken", server.LogText());
        Assert.Contains("attempt 2 of 3", server.LogText());
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).Status);
    }

    [Fact]
    public async Task A_port_held_by_another_Weir_server_is_not_taken_for_the_new_servers_own()
    {
        await using var holder = await WeirServer.StartNewAsync();
        var taken = holder.BaseUrl.Port;
        var picks = new Queue<int>([taken]);

        await using var server = await WeirServer.StartWithPortsAsync(null, () => picks.Count > 0 ? picks.Dequeue() : WeirServer.FreePort());

        Assert.NotEqual(taken, server.BaseUrl.Port);
        Assert.Contains($"port {taken} was taken", server.LogText());
        Assert.True(server.IsRunning);
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).Status);
    }

    [Fact]
    public async Task Servers_that_pick_the_same_port_at_once_each_end_up_on_a_port_of_their_own()
    {
        const int Servers = 4;
        var shared = WeirServer.FreePort();
        var starts = Enumerable.Range(0, Servers).Select(_ =>
        {
            var firstPick = true;
            return Task.Run(() => WeirServer.StartWithPortsAsync(null, () =>
            {
                var port = firstPick ? shared : WeirServer.FreePort();
                firstPick = false;
                return port;
            }));
        }).ToList();
        try
        {
            var servers = await Task.WhenAll(starts);

            Assert.Equal(Servers, servers.Select(server => server.BaseUrl.Port).Distinct().Count());
            foreach (var server in servers)
            {
                Assert.True(server.IsRunning);
                using var client = await server.CreateAdminClientAsync();
            }
        }
        finally
        {
            foreach (var start in starts.Where(start => start.IsCompletedSuccessfully))
            {
                await (await start).DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_port_that_stays_taken_fails_after_three_attempts_with_the_servers_log()
    {
        using var occupied = Occupy();
        var picks = 0;

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            WeirServer.StartWithPortsAsync(null, () =>
            {
                picks++;
                return occupied.Port;
            }));

        Assert.Equal(WeirServer.MaxStartAttempts, picks);
        Assert.Contains("exited with code", failure.Message);
        Assert.Contains("before it was ready", failure.Message);
        Assert.Contains("address already in use", failure.Message);
    }

    [Fact]
    public async Task A_server_that_refuses_to_start_for_another_reason_fails_at_once()
    {
        var picks = 0;
        var refused = new Dictionary<string, string> { [RefusedSetting] = "not an address" };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WeirServer.StartWithPortsAsync(refused, () =>
            {
                picks++;
                return WeirServer.FreePort();
            }));

        Assert.Equal(1, picks);
        Assert.Contains("exited with code 1 before it was ready", failure.Message);
        Assert.Contains(RefusedSetting, failure.Message);
    }

    [Theory]
    [InlineData("System.IO.IOException: Failed to bind to address http://127.0.0.1:5000: address already in use.", true)]
    [InlineData("---> Microsoft.AspNetCore.Connections.AddressInUseException: Only one usage of each socket address", true)]
    [InlineData("Failed to bind to address http://127.0.0.1:80: permission denied.", false)]
    [InlineData("Weir cannot start: Invalid port 99999: expected a number from 1 to 65535.", false)]
    [InlineData("", false)]
    public void Only_an_address_in_use_failure_counts_as_a_port_clash(string log, bool expected) =>
        Assert.Equal(expected, ServerBindFailure.IsPortInUse(log));

    [Theory]
    [InlineData("2026-10-14 03:22:08,912 INFO Weir.Host.Startup Weir 0.0.1-dev listening on 127.0.0.1:58123", 58123, true)]
    [InlineData("2026-10-14 03:22:08,912 INFO Weir.Host.Startup Weir 0.0.1-dev listening on 127.0.0.1:58123", 8123, false)]
    [InlineData("2026-10-14 03:22:08,912 INFO Weir.Host.Startup Weir 0.0.1-dev listening on 127.0.0.1:58123", 5812, false)]
    [InlineData("2026-10-14 03:22:08,911 INFO Microsoft.Hosting.Lifetime Now listening on: http://127.0.0.1:58123", 58123, false)]
    [InlineData("", 58123, false)]
    public void Only_the_line_a_server_writes_when_it_listens_on_the_port_counts(string line, int port, bool expected)
    {
        using var folder = new TemporaryFolder();
        using var log = new ServerLog(Path.Combine(folder.Path, "server.log"));
        log.Note(line);

        Assert.Equal(expected, log.SaysListeningOn("127.0.0.1", port));
    }

    private static Occupant Occupy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Occupant(listener);
    }

    private sealed class Occupant(TcpListener listener) : IDisposable
    {
        public int Port { get; } = ((IPEndPoint)listener.LocalEndpoint).Port;

        public void Dispose() => listener.Stop();
    }
}
