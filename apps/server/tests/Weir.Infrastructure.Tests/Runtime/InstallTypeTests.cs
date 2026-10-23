using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>How Weir tells a Docker, a packaged Windows and a source install apart.</summary>
public sealed class InstallTypeTests
{
    private static bool NothingExists(string path) => false;

    private static bool OnlyTheDockerMarker(string path) => path == "/.dockerenv";

    [Theory]
    [InlineData("windows", "windows")]
    [InlineData(" Docker ", "docker")]
    [InlineData("SOURCE", "source")]
    public void A_runtime_the_owner_named_is_the_install_type_whatever_the_machine_says(string named, string expected)
    {
        Assert.Equal(expected, UpdateFiles.DetectInstallType(named, OnlyTheDockerMarker, singleFile: true, windows: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("made-up")]
    public void A_machine_with_a_docker_marker_is_a_docker_install_when_the_owner_named_nothing_usable(string? named)
    {
        Assert.Equal("docker", UpdateFiles.DetectInstallType(named, OnlyTheDockerMarker, singleFile: true, windows: true));
    }

    [Theory]
    [InlineData(true, true, "windows")]
    [InlineData(true, false, "source")]
    [InlineData(false, true, "source")]
    [InlineData(false, false, "source")]
    public void Only_a_single_file_build_on_Windows_is_the_packaged_Windows_app(bool singleFile, bool windows, string expected)
    {
        Assert.Equal(expected, UpdateFiles.DetectInstallType(null, NothingExists, singleFile, windows));
    }

    [Fact]
    public void The_machine_this_runs_on_is_asked_when_no_answer_is_supplied()
    {
        Assert.Contains(UpdateFiles.DetectInstallType(null), new[] { "docker", "source", "windows" });
    }
}
