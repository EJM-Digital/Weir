using System.Net;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>What a start leaves in System, Logs: nothing an operator would take for a problem.</summary>
[ContractArea("system")]
public sealed class StartupLogTests
{
    /// <summary>
    /// The .NET container images name a port for Kestrel (<c>ASPNETCORE_HTTP_PORTS=8080</c>) that Weir does not listen on. Weir listens where
    /// <c>--host</c> and <c>--port</c> say, so the setting must not reach Kestrel, which would log a warning at every start of the Docker image.
    /// </summary>
    [Theory]
    [InlineData("ASPNETCORE_HTTP_PORTS", "8080")]
    [InlineData("ASPNETCORE_URLS", "http://*:8080")]
    public async Task An_address_the_environment_gives_Kestrel_adds_no_warning_to_the_log(string variable, string value)
    {
        await using var server = await WeirServer.StartNewAsync(new Dictionary<string, string> { [variable] = value });
        using var admin = await server.CreateAdminClientAsync();

        var response = await admin.GetAsync($"{WeirClient.Api}/system/log", ("level", "warning,error"), ("limit", 100));

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var overriding = response.Fields["items"]!.AsArray().Select(item => (string)item!["title"]!).Where(title => title.Contains("Overriding", StringComparison.Ordinal)).ToList();
        Assert.True(overriding.Count == 0, $"Kestrel logged that it is overriding an address: {string.Join(" | ", overriding)}");
    }
}
