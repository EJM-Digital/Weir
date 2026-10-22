using Weir.Infrastructure.Runtime;

namespace Weir.Api.Tests.Startup;

/// <summary>A start the Windows tray watches: it asks for the progress on standard output as well as in the file, and the server still comes up.</summary>
public sealed class StartupProgressChannelTests
{
    [Fact]
    public async Task A_start_asked_to_report_its_progress_on_standard_output_still_reaches_ready()
    {
        await using var server = await WeirTestServer.StartAsync([(StartupProgress.PipeVariable, "1")]);

        var progress = await File.ReadAllTextAsync(Path.Join(server.Home, StartupProgress.FileName));
        Assert.Contains("\"phase\":\"ready\"", progress, StringComparison.Ordinal);
    }
}
