using FsCheck;
using FsCheck.Fluent;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.TestSupport;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The paths a pass trusts to stay inside a library's folders (<see cref="RemuxPassPaths"/> and <see cref="PathContainment"/>), tried on a
/// real folder with relative paths written every way a client can write one: either separator, <c>..</c>, drive letters, device names,
/// white space and NUL.
/// </summary>
public sealed class RemuxPassPathsPropertyTests : IDisposable
{
    private static readonly Gen<string> Part = Gen.Frequency(
        (6, PathGenerators.Name),
        (2, Gen.Constant("..")),
        (1, Gen.Constant(".")),
        (1, Gen.Constant(string.Empty)),
        (1, Gen.Elements("...", ".. ", " .. ", "CON", "nul", "COM1", "a:b", "C:", "~", "a\0b", "\0", "name.", "name ", "é", "..\\", "/..")));

    private static readonly Gen<string> RelativeText =
        from count in Gen.Choose(0, 6)
        from parts in Gen.ArrayOf(Part, count)
        from separators in Gen.ArrayOf(PathGenerators.Separator, count + 1)
        from leading in Gen.Elements(string.Empty, string.Empty, "/", "\\", " ", "C:\\", "//", "\\\\")
        select leading + string.Concat(parts.Select((part, index) => part + separators[index]));

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_relative_path_resolves_to_a_path_inside_the_folder_or_is_refused_as_an_argument_and_never_leads_out()
    {
        var root = RemuxPassPaths.Resolve(Directory.CreateDirectory(_folder.Join("watched")).FullName);
        PropertyChecks.ForAll(RelativeText, relative =>
        {
            string resolved;
            try
            {
                resolved = RemuxPassPaths.ResolveMediaFileUnderRoot(root, relative);
            }
            catch (ArgumentException)
            {
                return;
            }

            Assert.True(RemuxPassPaths.IsUnder(resolved, root), resolved);
            Assert.True(PathContainment.IsUnder(root, resolved) || RemuxPassPaths.SamePath(resolved, root), resolved);
            Assert.False(resolved.Contains('\0', StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Names_joined_with_either_separator_resolve_to_those_names_below_the_folder_in_order()
    {
        var root = RemuxPassPaths.Resolve(Directory.CreateDirectory(_folder.Join("library")).FullName);
        var names =
            from count in Gen.Choose(1, 5)
            from parts in Gen.ArrayOf(PathGenerators.Name, count)
            from relative in PathGenerators.Render(string.Empty, parts, varyCase: false, trailingSeparator: true)
            select (parts, relative);

        PropertyChecks.ForAll(names, found =>
        {
            var resolved = RemuxPassPaths.ResolveMediaFileUnderRoot(root, found.relative);
            Assert.Equal(string.Join(Path.DirectorySeparatorChar, found.parts), RemuxPassPaths.RelativeTo(resolved, root));
            Assert.Equal(string.Join('/', found.parts), RemuxPassPaths.Posix(RemuxPassPaths.RelativeTo(resolved, root)!));
        });
    }

    [Fact]
    public void A_path_is_under_a_folder_when_it_is_the_folder_or_below_it_and_not_when_it_only_starts_with_its_name()
    {
        var root = RemuxPassPaths.Resolve(Directory.CreateDirectory(_folder.Join("movies")).FullName);
        var below =
            from count in Gen.Choose(0, 4)
            from parts in Gen.ArrayOf(PathGenerators.Name, count)
            from sibling in PathGenerators.Name
            select (parts, sibling);

        PropertyChecks.ForAll(below, found =>
        {
            var inside = Path.Join([root, .. found.parts]);
            Assert.True(RemuxPassPaths.IsUnder(inside, root));
            Assert.Equal(found.parts.Length == 0, RemuxPassPaths.SamePath(inside, root));
            Assert.Equal(found.parts.Length > 0, PathContainment.IsUnder(root, inside));

            var lookalike = root + found.sibling;
            Assert.False(RemuxPassPaths.IsUnder(lookalike, root));
            Assert.False(PathContainment.IsUnder(root, Path.Join(lookalike, "film.mkv")));
            Assert.False(RemuxPassPaths.IsUnder(root, inside) && found.parts.Length > 0);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(RemuxPassPaths.IsUnder(inside.ToUpperInvariant(), root));
            }
        });
    }

    [Fact]
    public void Climbing_out_with_parent_segments_is_never_inside_even_after_stepping_in_first()
    {
        var root = RemuxPassPaths.Resolve(Directory.CreateDirectory(_folder.Join("tv")).FullName);
        var climbs =
            from inward in PathGenerators.Names(0, 3)
            from extra in Gen.Choose(1, 3)
            from separator in PathGenerators.Separator
            select string.Join(separator, inward.Concat(Enumerable.Repeat("..", inward.Count + extra)).Append("film.mkv"));

        PropertyChecks.ForAll(climbs, relative =>
        {
            var target = Path.Join(root, relative.Replace('\\', '/'));
            Assert.False(PathContainment.IsUnder(root, target), target);
            Assert.False(RemuxPassPaths.IsUnder(target, root), target);
        });
    }
}
