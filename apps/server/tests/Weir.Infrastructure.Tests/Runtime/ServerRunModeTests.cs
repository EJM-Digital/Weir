using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>How Weir tells a service, an app and a container apart.</summary>
public sealed class ServerRunModeTests
{
    [Theory]
    [InlineData("docker", true, false, "docker")]
    [InlineData("docker", false, true, "docker")]
    [InlineData("docker", false, false, "docker")]
    [InlineData("windows", true, false, "service")]
    [InlineData("source", false, true, "service")]
    [InlineData("source", true, true, "service")]
    [InlineData("windows", false, false, "app")]
    [InlineData("source", false, false, "app")]
    public void A_container_is_docker_whatever_started_it_and_otherwise_a_service_manager_makes_a_service(
        string installType, bool startedByWindowsService, bool startedBySystemd, string expected)
    {
        Assert.Equal(expected, ServerRunMode.Detect(installType, startedByWindowsService, startedBySystemd).Name);
    }
}
