using System.Reflection;
using System.Reflection.Emit;

using Xunit;

namespace Weir.Tray.Tests;

public sealed class UpdateChannelTests
{
    [Theory]
    [InlineData("1.0.0-rc.1")]
    [InlineData("1.0.0-beta.2+abc123")]
    [InlineData("0.0.1-dev")]
    public void A_pre_release_install_is_offered_pre_releases(string runningVersion) =>
        Assert.True(UpdateChannel.IncludesPreReleases(runningVersion));

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("3.2.16")]
    [InlineData("1.0.0+abc-123")]
    [InlineData("")]
    [InlineData(null)]
    public void A_stable_install_is_offered_only_stable_releases(string? runningVersion) =>
        Assert.False(UpdateChannel.IncludesPreReleases(runningVersion));

    [Fact]
    public void The_version_a_build_carries_is_the_one_it_was_built_as_and_an_assembly_with_none_has_none()
    {
        Assert.Equal(typeof(UpdateChannel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, UpdateChannel.VersionOf(typeof(UpdateChannel).Assembly));
        Assert.Null(UpdateChannel.VersionOf(AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NoVersion"), AssemblyBuilderAccess.Run)));
    }

    [Fact]
    public void The_source_for_this_build_follows_the_channel_of_the_version_it_was_built_as()
    {
        Assert.NotNull(UpdateChannel.SourceForThisBuild(ProjectLocation.RepositoryUrl));
    }
}
