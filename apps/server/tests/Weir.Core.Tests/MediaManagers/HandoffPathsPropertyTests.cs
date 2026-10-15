using FsCheck;
using FsCheck.Fluent;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.TestSupport;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="HandoffPaths"/> over folders and files written the way different hosts write them: either kind of separator, doubled and
/// trailing separators, drive letters and shares, any case, stray white space, <c>.</c> and <c>..</c>.
/// </summary>
public sealed class HandoffPathsPropertyTests
{
    private sealed record Inside(string Folder, string File, string Expected);

    private sealed record Pair(string Folder, string File);

    private static readonly Gen<Inside> InsideTheFolder =
        from root in Gen.Elements(PathGenerators.Roots)
        from folderNames in PathGenerators.Names(1, 4)
        from restNames in PathGenerators.Names(1, 4)
        from dotsBefore in Gen.ArrayOf(Gen.Elements(false, false, true), restNames.Count)
        from folderText in PathGenerators.Render(root, folderNames, varyCase: false, trailingSeparator: true)
        from folderPrefix in PathGenerators.Render(root, folderNames, varyCase: true)
        from folderPadded in PathGenerators.Padded(folderText)
        from separator in PathGenerators.Separator
        from rest in PathGenerators.Render(string.Empty, WithDots(restNames, dotsBefore), varyCase: false)
        from filePadded in PathGenerators.Padded(folderPrefix + separator + rest)
        select new Inside(folderPadded, filePadded, string.Join('/', restNames));

    private static readonly Gen<Pair> Wandering =
        from root in Gen.Elements(PathGenerators.Roots)
        from folderNames in PathGenerators.Names(1, 3)
        from folderText in PathGenerators.Render(root, folderNames, varyCase: false)
        from startsInside in Gen.Elements(true, true, false)
        from head in startsInside ? PathGenerators.Render(root, folderNames, varyCase: true) : PathGenerators.Render(root, Array.Empty<string>(), varyCase: false)
        from count in Gen.Choose(0, 5)
        from tail in Gen.ArrayOf(PathGenerators.WanderingPart, count)
        from rest in PathGenerators.Render(string.Empty, tail, varyCase: false)
        select new Pair(folderText, head + "/" + rest);

    private static readonly Gen<string> Soup = Gen.Choose(0, 18).SelectMany(length => Gen.ArrayOf(Gen.Elements("aB/\\.. :é\t".ToCharArray()), length)).Select(chars => new string(chars));

    [Fact]
    public void A_file_inside_the_folder_resolves_to_its_own_path_below_it()
    {
        PropertyChecks.ForAll(InsideTheFolder, found =>
        {
            var result = HandoffPaths.RelativeMediaPathForHandoff(found.Folder, found.File);
            Assert.True(result.Ok, result.Problem);
            Assert.Equal(found.Expected, result.RelativeMediaPath);
            Assert.Null(result.Problem);
        });
    }

    [Fact]
    public void The_kind_of_separator_and_the_case_of_the_folder_do_not_change_the_answer()
    {
        PropertyChecks.ForAll(Wandering, pair =>
        {
            var answer = HandoffPaths.RelativeMediaPathForHandoff(pair.Folder, pair.File);
            Assert.Equal(answer, HandoffPaths.RelativeMediaPathForHandoff(pair.Folder.Replace('/', '\\'), pair.File.Replace('/', '\\')));
            Assert.Equal(answer, HandoffPaths.RelativeMediaPathForHandoff(pair.Folder.Replace('\\', '/'), pair.File.Replace('\\', '/')));
            Assert.Equal(answer, HandoffPaths.RelativeMediaPathForHandoff(pair.Folder.ToUpperInvariant(), pair.File));
            Assert.Equal(answer, HandoffPaths.RelativeMediaPathForHandoff(pair.Folder.ToLowerInvariant(), pair.File));
        });
    }

    [Fact]
    public void A_file_in_another_folder_is_refused()
    {
        var differing =
            from root in Gen.Elements(PathGenerators.Roots)
            from folderNames in PathGenerators.Names(1, 4)
            from index in Gen.Choose(0, folderNames.Count - 1)
            from other in PathGenerators.Name.Where(name => !name.Equals(folderNames[index], StringComparison.OrdinalIgnoreCase))
            from restNames in PathGenerators.Names(1, 3)
            from folderText in PathGenerators.Render(root, folderNames, varyCase: false)
            from fileText in PathGenerators.Render(root, [.. folderNames.Take(index), other, .. folderNames.Skip(index + 1), .. restNames], varyCase: true)
            select new Pair(folderText, fileText);

        PropertyChecks.ForAll(differing, pair =>
        {
            var result = HandoffPaths.RelativeMediaPathForHandoff(pair.Folder, pair.File);
            Assert.False(result.Ok);
            Assert.Contains("watched folder", result.Problem, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_folder_itself_and_anything_above_it_are_refused()
    {
        var shorter =
            from root in Gen.Elements(PathGenerators.Roots)
            from folderNames in PathGenerators.Names(1, 4)
            from kept in Gen.Choose(1, folderNames.Count)
            from folderText in PathGenerators.Render(root, folderNames, varyCase: false)
            from fileText in PathGenerators.Render(root, [.. folderNames.Take(kept)], varyCase: true, trailingSeparator: true)
            select new Pair(folderText, fileText);

        PropertyChecks.ForAll(shorter, pair =>
        {
            var result = HandoffPaths.RelativeMediaPathForHandoff(pair.Folder, pair.File);
            Assert.False(result.Ok);
        });
    }

    [Fact]
    public void A_path_that_climbs_out_of_the_folder_is_refused_wherever_the_climb_is()
    {
        var climbing =
            from root in Gen.Elements(PathGenerators.Roots)
            from folderNames in PathGenerators.Names(1, 3)
            from before in PathGenerators.Names(0, 2)
            from after in PathGenerators.Names(0, 2)
            from folderText in PathGenerators.Render(root, folderNames, varyCase: false)
            from fileText in PathGenerators.Render(root, [.. folderNames, .. before, "..", .. after, "film.mkv"], varyCase: true)
            select new Pair(folderText, fileText);

        PropertyChecks.ForAll(climbing, pair =>
        {
            var result = HandoffPaths.RelativeMediaPathForHandoff(pair.Folder, pair.File);
            Assert.False(result.Ok, result.RelativeMediaPath);
        });
    }

    [Fact]
    public void Whatever_it_is_given_it_either_names_a_path_below_the_folder_or_says_why_not()
    {
        PropertyChecks.ForAll(Soup, Soup, (folder, file) =>
        {
            var result = HandoffPaths.RelativeMediaPathForHandoff(folder, file);
            if (!result.Ok)
            {
                Assert.False(string.IsNullOrEmpty(result.Problem));
                Assert.False(result.Problem!.Contains('/', StringComparison.Ordinal));
                return;
            }

            var relative = result.RelativeMediaPath!.Split('/');
            Assert.DoesNotContain(relative, part => part is "" or "." or "..");
            Assert.False(result.RelativeMediaPath.Contains('\\', StringComparison.Ordinal));

            var fileParts = PartsOf(file);
            var folderParts = PartsOf(folder);
            Assert.True(
                fileParts.Take(folderParts.Count).SequenceEqual(folderParts, StringComparer.OrdinalIgnoreCase),
                "The file does not start with the folder.");
            Assert.Equal(fileParts.Skip(folderParts.Count).Where(part => part != ".").ToArray(), relative);
        });
    }

    private static List<string> PartsOf(string path) =>
        [.. WireStrings.Strip(path).Replace('\\', '/').Split('/').Where(part => part.Length > 0)];

    private static List<string> WithDots(IReadOnlyList<string> names, bool[] dotsBefore)
    {
        var parts = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            if (dotsBefore[i])
            {
                parts.Add(".");
            }

            parts.Add(names[i]);
        }

        return parts;
    }
}
