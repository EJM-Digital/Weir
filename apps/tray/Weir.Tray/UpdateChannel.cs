using System.Reflection;
using Velopack.Sources;

namespace Weir.Tray;

/// <summary>Which GitHub releases this install updates to: stable releases only, unless it is itself a pre-release.</summary>
static class UpdateChannel
{
    /// <summary>
    /// The release source for an install running <paramref name="runningVersion"/>. An install on <c>1.0.0-rc.1</c> is offered
    /// pre-releases, or it would never see <c>1.0.0-rc.2</c>.
    /// </summary>
    internal static ReleaseFeedSource SourceFor(string repoUrl, string? runningVersion, IFileDownloader? downloader = null) =>
        new(repoUrl, IncludesPreReleases(runningVersion), downloader);

    internal static ReleaseFeedSource SourceForThisBuild(string repoUrl) =>
        SourceFor(repoUrl, VersionOf(typeof(UpdateChannel).Assembly));

    /// <summary>The version an assembly was built as, or null when it carries none.</summary>
    internal static string? VersionOf(System.Reflection.Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// Whether an install running <paramref name="runningVersion"/> is offered pre-releases. A pre-release has a hyphen in its
    /// version (<c>1.0.0-rc.1</c>); the numbers before it never do, and build metadata (<c>+abc123</c>) is not part of the check.
    /// </summary>
    internal static bool IncludesPreReleases(string? runningVersion)
    {
        var withoutMetadata = (runningVersion ?? string.Empty).Split('+')[0];
        return withoutMetadata.Contains('-', StringComparison.Ordinal);
    }
}
