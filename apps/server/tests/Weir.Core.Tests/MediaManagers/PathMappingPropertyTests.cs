using FsCheck;
using FsCheck.Fluent;
using Weir.Core.MediaManagers;
using Weir.TestSupport;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="DownloadedScanRules.TranslateOutputPath"/> over remote path mappings between Windows and POSIX hosts, of any depth
/// (a drive or the filesystem root included), with the output written in either style.
/// </summary>
public sealed class PathMappingPropertyTests
{
    private static readonly string[] MappingRoots = ["/", "C:\\", "d:/", "\\\\nas\\media\\", "//nas/media/"];

    private sealed record Translation(RemotePathMappingEntry Mapping, string Output, IReadOnlyList<string> Below);

    private static readonly Gen<Translation> OutputInsideTheLocalFolder =
        from localRoot in Gen.Elements(MappingRoots)
        from remoteRoot in Gen.Elements(MappingRoots)
        from localNames in PathGenerators.Names(0, 3)
        from remoteNames in PathGenerators.Names(0, 3)
        from below in PathGenerators.Names(0, 3)
        from local in PathGenerators.Render(localRoot, localNames, varyCase: false, trailingSeparator: true)
        from remote in PathGenerators.Render(remoteRoot, remoteNames, varyCase: false, trailingSeparator: true)
        from output in PathGenerators.Render(localRoot, [.. localNames, .. below], varyCase: false)
        select new Translation(new RemotePathMappingEntry("client", remote, local), output, below);

    [Fact]
    public void An_output_in_the_local_folder_is_the_same_place_below_the_remote_folder()
    {
        PropertyChecks.ForAll(OutputInsideTheLocalFolder, found =>
        {
            var translated = new ArrOsPath(DownloadedScanRules.TranslateOutputPath(found.Output, [found.Mapping]));
            var remote = new ArrOsPath(found.Mapping.RemotePath);

            Assert.Equal([.. remote.Segments, .. found.Below], translated.Segments);
            Assert.True(translated.IsRooted);
            Assert.True(remote.Contains(translated));
        });
    }

    [Fact]
    public void Translating_with_the_mapping_the_other_way_round_gives_the_output_back()
    {
        PropertyChecks.ForAll(OutputInsideTheLocalFolder, found =>
        {
            var there = DownloadedScanRules.TranslateOutputPath(found.Output, [found.Mapping]);
            var reversed = new RemotePathMappingEntry(found.Mapping.Host, found.Mapping.LocalPath, found.Mapping.RemotePath);
            var back = DownloadedScanRules.TranslateOutputPath(there, [reversed]);
            Assert.Equal(new ArrOsPath(found.Output).Segments, new ArrOsPath(back).Segments);
        });
    }

    [Fact]
    public void A_folder_that_only_starts_with_the_local_folders_name_is_not_inside_it()
    {
        var sibling =
            from found in OutputInsideTheLocalFolder
            from extra in PathGenerators.Name
            let localNames = new ArrOsPath(found.Mapping.LocalPath).Segments
            where localNames.Length > 0
            select (found.Mapping, Output: found.Mapping.LocalPath.TrimEnd('/', '\\') + extra + "/film.mkv");

        PropertyChecks.ForAll(sibling, found =>
            Assert.Equal(found.Output, DownloadedScanRules.TranslateOutputPath(found.Output, [found.Mapping])));
    }
}
