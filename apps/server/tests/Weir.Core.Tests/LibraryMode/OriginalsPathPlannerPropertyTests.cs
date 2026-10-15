using FsCheck;
using FsCheck.Fluent;
using Weir.Core.LibraryMode;
using Weir.TestSupport;

namespace Weir.Core.Tests.LibraryMode;

/// <summary>
/// <see cref="OriginalsPathPlanner"/> over library folders written in either style: a kept original is never put where the file it
/// came from is, never where another file's original goes, and always inside the originals folder.
/// </summary>
public sealed class OriginalsPathPlannerPropertyTests
{
    private static readonly string[] Roots = ["/", "C:\\", "d:/", "\\\\nas\\media\\", "//nas/media/"];

    private sealed record Library(string Folder, IReadOnlyList<string> FolderNames, string? Originals);

    private static readonly Gen<Library> AnyLibrary =
        from root in Gen.Elements(Roots)
        from names in PathGenerators.Names(1, 3)
        from separator in Gen.Elements("/", "\\")
        from trailing in Gen.Elements(string.Empty, "/", "\\")
        from originals in Gen.OneOf(Gen.Constant<string?>(null), PathGenerators.Names(1, 2).Select(parts => (string?)("/kept/" + string.Join('/', parts))))
        select new Library(root + string.Join(separator, names) + trailing, names, originals);

    private static Gen<string> FileIn(Library library, IReadOnlyList<string> below, string separator) =>
        Gen.Constant(library.Folder.TrimEnd('/', '\\') + separator + string.Join(separator, below));

    private static string PathOf(Library library, IReadOnlyList<string> names) =>
        library.Folder.TrimEnd('/', '\\') + "/" + string.Join('/', names);

    [Fact]
    public void A_file_in_a_library_folder_is_found_in_it_and_never_in_a_folder_that_only_shares_its_name()
    {
        var cases =
            from library in AnyLibrary
            from below in PathGenerators.Names(1, 3)
            from separator in Gen.Elements("/", "\\")
            from file in FileIn(library, below, separator)
            from extra in PathGenerators.Name
            select (library, file, extra);

        PropertyChecks.ForAll(cases, found =>
        {
            Assert.True(OriginalsPathPlanner.IsWithin(found.library.Folder, found.file));
            Assert.Equal(found.library.Folder, OriginalsPathPlanner.ContainingFolder(["/elsewhere", found.library.Folder], found.file));
            var lookalike = found.library.Folder.TrimEnd('/', '\\') + found.extra + "/film.mkv";
            Assert.False(OriginalsPathPlanner.IsWithin(found.library.Folder, lookalike));
            Assert.Null(OriginalsPathPlanner.ContainingFolder([found.library.Folder], lookalike));
        });
    }

    [Fact]
    public void A_path_with_parent_segments_is_inside_a_folder_exactly_when_the_system_settles_it_there()
    {
        // No more climbs than the folder is deep, so the system's own settling never meets the top of the filesystem.
        var wandering =
            from names in PathGenerators.Names(2, 3)
            from extra in PathGenerators.Names(1, 2)
            from count in Gen.Choose(1, 6)
            from steps in Gen.ArrayOf(Gen.Frequency<string>((4, Gen.Elements([.. names, .. extra])), (3, Gen.Constant("..")), (1, Gen.Constant("."))), count)
            let climbs = steps.Count(step => step == "..")
            where climbs <= names.Count
            select (names, steps);

        PropertyChecks.ForAll(wandering, found =>
        {
            var folder = Path.GetFullPath(Path.Join([Path.GetTempPath(), "libraries", .. found.names]));
            var path = Path.Join([folder, .. found.steps]);
            var settled = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var expected = settled.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                           settled.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(expected, OriginalsPathPlanner.IsWithin(folder, path));
        });
    }

    [Fact]
    public void A_kept_original_never_has_a_parent_segment_and_is_always_inside_the_originals_folder_whatever_the_file_path_says()
    {
        var cases =
            from library in AnyLibrary
            from count in Gen.Choose(1, 6)
            from steps in Gen.ArrayOf(PathGenerators.WanderingPart.Where(step => step.Length > 0), count)
            from inside in Gen.Elements(true, false)
            from separator in Gen.Elements("/", "\\")
            select (library, file: (inside ? library.Folder.TrimEnd('/', '\\') : "/elsewhere") + separator + string.Join(separator, steps));

        PropertyChecks.ForAll(cases, found =>
        {
            var destination = OriginalsPathPlanner.DestinationPath(found.library.Folder, found.library.Originals, found.file);
            var folder = OriginalsPathPlanner.DestinationFolder(found.library.Folder, found.library.Originals);

            Assert.DoesNotContain(destination[folder.TrimEnd('/', '\\').Length..].Split('/', '\\'), part => part == "..");
            Assert.True(OriginalsPathPlanner.IsWithin(folder, destination), destination);
        });
    }

    [Fact]
    public void A_kept_original_is_inside_the_originals_folder_and_is_not_the_file_it_came_from()
    {
        var cases =
            from library in AnyLibrary.Where(candidate => candidate.Originals is null)
            from below in PathGenerators.Names(1, 3)
            from separator in Gen.Elements("/", "\\")
            from file in FileIn(library, below, separator)
            select (library, file, below);

        PropertyChecks.ForAll(cases, found =>
        {
            var destination = OriginalsPathPlanner.DestinationPath(found.library.Folder, found.library.Originals, found.file);
            var folder = OriginalsPathPlanner.DestinationFolder(found.library.Folder, found.library.Originals);

            Assert.NotEqual(found.file, destination, StringComparer.OrdinalIgnoreCase);
            Assert.True(OriginalsPathPlanner.IsWithin(folder, destination));
            Assert.EndsWith(found.below[^1], destination, StringComparison.Ordinal);
            Assert.True(OriginalsPathPlanner.IsWithin(found.library.Folder, folder));
        });
    }

    [Fact]
    public void Two_different_files_never_have_their_originals_kept_at_the_same_place()
    {
        var cases =
            from library in AnyLibrary
            from first in PathGenerators.Names(1, 3)
            from second in PathGenerators.Names(1, 3)
            select (library, first, second);

        PropertyChecks.ForAll(cases, found =>
        {
            var a = PathOf(found.library, found.first);
            var b = PathOf(found.library, found.second);
            if (found.first.SequenceEqual(found.second, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Assert.NotEqual(
                OriginalsPathPlanner.DestinationPath(found.library.Folder, found.library.Originals, a),
                OriginalsPathPlanner.DestinationPath(found.library.Folder, found.library.Originals, b),
                StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Every_attempt_at_a_name_is_a_different_name_in_the_same_folder_with_the_same_extension()
    {
        var cases =
            from folder in PathGenerators.Names(0, 3)
            from stem in PathGenerators.Name
            from extension in Gen.Elements(".mkv", ".tar.gz", string.Empty, ".")
            from separator in Gen.Elements("/", "\\")
            select (folder.Count == 0 ? string.Empty : string.Join(separator, folder) + separator) + stem + extension;

        PropertyChecks.ForAll(cases, path =>
        {
            var candidates = Enumerable.Range(1, 12).Select(attempt => OriginalsPathPlanner.CandidateAt(path, attempt)).ToList();
            var folder = path[..(path.LastIndexOfAny(['/', '\\']) + 1)];
            var name = path[folder.Length..];
            var dot = name.LastIndexOf('.');
            var extension = dot <= 0 ? string.Empty : name[dot..];

            Assert.Equal(path, candidates[0]);
            Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
            Assert.All(candidates, candidate =>
            {
                Assert.StartsWith(folder, candidate, StringComparison.Ordinal);
                Assert.Equal(-1, candidate[folder.Length..].IndexOfAny(['/', '\\']));
                Assert.EndsWith(extension, candidate, StringComparison.Ordinal);
            });
        });
    }
}
