namespace Weir.Contract.Tests.Harness;

/// <summary>Checks that a server can be asked to stop and ends by itself, which is how a coverage run lets it write what it ran.</summary>
[ContractArea("harness")]
public sealed class ServerShutdownTests
{
    [Fact]
    public async Task A_server_asked_to_stop_ends_by_itself()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var process = System.Diagnostics.Process.GetProcessById(server.ProcessId);

        Assert.True(ServerShutdown.Request(process));

        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(ceiling.Token);
        Assert.False(server.IsRunning);
        await server.WaitForLogLineAsync(line => line.Contains("Application is shutting down", StringComparison.Ordinal), "the server saying it is shutting down");
    }
}
