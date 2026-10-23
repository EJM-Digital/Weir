using System.Reflection;

namespace Weir.Core;

/// <summary>
/// The version the server reports: <c>WEIR_VERSION</c> when set,
/// otherwise the build's version, which MSBuild takes from <c>WeirVersion</c> in <c>apps/server/Directory.Build.props</c>.
/// A release reports its plain version. A CI build stamped for the golden path keeps its commit as build metadata
/// (<c>1.0.0-rc.4+abc1234</c>), so the record names the exact build; version comparisons ignore that part.
/// </summary>
public static class WeirVersion
{
    public static string Resolve(string? versionOverride) =>
        string.IsNullOrWhiteSpace(versionOverride) ? BuildVersion : versionOverride.Trim();

    public static string BuildVersion { get; } = ReadBuildVersion();

    private static string ReadBuildVersion()
    {
        return typeof(WeirVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    }
}
