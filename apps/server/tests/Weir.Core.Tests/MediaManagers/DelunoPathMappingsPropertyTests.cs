using FsCheck;
using FsCheck.Fluent;
using Weir.Core.MediaManagers;
using Weir.TestSupport;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="DelunoPathMappings.Apply"/> and <see cref="DownloadClientPaths"/> over folders written in either style, at any depth,
/// with several mappings that overlap: a result stays inside the folder its mapping names, and the longest mapping wins.
/// </summary>
public sealed class DelunoPathMappingsPropertyTests
{
    private static readonly string[] Roots = ["/", "C:\\", "d:/", "\\\\nas\\media\\", "//nas/media/"];

    private sealed record Mapped(DelunoPathMapping Mapping, string Inside, IReadOnlyList<string> Below);

    private static readonly Gen<Mapped> InsideAMapping =
        from delunoRoot in Gen.Elements(Roots)
        from weirRoot in Gen.Elements(Roots)
        from delunoNames in PathGenerators.Names(0, 3)
        from weirNames in PathGenerators.Names(0, 3)
        from below in PathGenerators.Names(0, 3)
        from deluno in PathGenerators.Render(delunoRoot, delunoNames, varyCase: false, trailingSeparator: true)
        from weir in PathGenerators.Render(weirRoot, weirNames, varyCase: false, trailingSeparator: true)
        from inside in PathGenerators.Render(delunoRoot, [.. delunoNames, .. below], varyCase: false)
        select new Mapped(new DelunoPathMapping(deluno, weir), inside, below);

    [Fact]
    public void A_folder_inside_a_mapped_folder_is_the_same_place_below_the_weir_side_folder()
    {
        PropertyChecks.ForAll(InsideAMapping, found =>
        {
            var mapped = DelunoPathMappings.Apply([found.Mapping], found.Inside);

            Assert.True(mapped.WasMapped);
            Assert.Equal(found.Inside, mapped.Original);
            Assert.Equal([.. new ArrOsPath(found.Mapping.ProcessorPath).Segments, .. found.Below], new ArrOsPath(mapped.Path).Segments);
        });
    }

    [Fact]
    public void Mapping_there_and_back_gives_the_folder_back()
    {
        PropertyChecks.ForAll(InsideAMapping, found =>
        {
            var there = DelunoPathMappings.Apply([found.Mapping], found.Inside);
            var reversed = new DelunoPathMapping(found.Mapping.ProcessorPath, found.Mapping.DelunoPath);

            Assert.Equal(new ArrOsPath(found.Inside).Segments, new ArrOsPath(DelunoPathMappings.Apply([reversed], there.Path).Path).Segments);
        });
    }

    [Fact]
    public void A_folder_that_only_starts_with_a_mapped_folders_name_is_left_as_it_is()
    {
        var lookalike =
            from found in InsideAMapping
            from extra in PathGenerators.Name
            where new ArrOsPath(found.Mapping.DelunoPath).Segments.Length > 1
            select (found.Mapping, Path: found.Mapping.DelunoPath.TrimEnd('/', '\\') + extra + "/film");

        PropertyChecks.ForAll(lookalike, found =>
        {
            var mapped = DelunoPathMappings.Apply([found.Mapping], found.Path);

            Assert.False(mapped.WasMapped);
            Assert.Equal(found.Path, mapped.Path);
        });
    }

    [Fact]
    public void The_longest_mapping_that_covers_the_folder_wins_and_of_two_as_long_the_earlier_does()
    {
        var overlapping =
            from root in Gen.Elements("/", "C:\\")
            from shared in PathGenerators.Names(1, 2)
            from deeper in PathGenerators.Name
            from below in PathGenerators.Name
            from order in Gen.Elements(true, false)
            select (root, shared, deeper, below, order);

        PropertyChecks.ForAll(overlapping, found =>
        {
            var outer = new DelunoPathMapping(found.root + string.Join('/', found.shared), "/outer");
            var inner = new DelunoPathMapping(found.root + string.Join('/', [.. found.shared, found.deeper]), "/inner");
            var twin = new DelunoPathMapping(inner.DelunoPath, "/twin");
            var mappings = found.order ? new[] { outer, inner, twin } : [twin, outer, inner];
            var folder = inner.DelunoPath + "/" + found.below;

            var mapped = DelunoPathMappings.Apply(mappings, folder);

            Assert.Equal(found.order ? inner : twin, mapped.Via);
            Assert.Equal((found.order ? "/inner/" : "/twin/") + found.below, mapped.Path);
        });
    }

    [Fact]
    public void A_download_clients_folder_joins_with_the_separator_it_already_uses_and_never_doubles_it()
    {
        var joined =
            from root in Gen.Elements("/", "C:\\", "\\\\nas\\share\\", string.Empty)
            from names in PathGenerators.Names(0, 3)
            from relative in PathGenerators.Names(1, 3)
            from separator in Gen.Elements("/", "\\")
            from trailing in Gen.Elements(string.Empty, "/", "\\", "\\\\")
            from leading in Gen.Elements(string.Empty, "/", "\\", "//")
            select (basePath: root + string.Join(separator, names) + trailing, relative: leading + string.Join(separator, relative));

        PropertyChecks.ForAll(joined, found =>
        {
            var result = DownloadClientPaths.Join(found.basePath, found.relative);
            var windows = found.basePath.Contains('\\', StringComparison.Ordinal) && !found.basePath.Contains('/', StringComparison.Ordinal);
            var separator = windows ? '\\' : '/';

            Assert.StartsWith(found.basePath.TrimEnd('/', '\\'), result, StringComparison.Ordinal);
            Assert.EndsWith(found.relative.TrimStart('/', '\\'), result, StringComparison.Ordinal);
            Assert.Equal(found.basePath.TrimEnd('/', '\\') + separator + found.relative.TrimStart('/', '\\'), result);
        });
    }

    [Fact]
    public void A_path_is_absolute_when_it_starts_with_a_separator_or_a_drive_letter_and_a_colon()
    {
        var anyText = Gen.Choose(0, 6).SelectMany(length => Gen.ArrayOf(Gen.Elements("aZ:/\\ .".ToCharArray()), length)).Select(chars => new string(chars));
        PropertyChecks.ForAll(anyText, text =>
        {
            var expected = text.Length > 0 && (text[0] is '/' or '\\' || (text.Length >= 2 && text[1] == ':' && char.IsAsciiLetter(text[0])));
            Assert.Equal(expected, DownloadClientPaths.IsAbsolute(text));
        });
    }
}
