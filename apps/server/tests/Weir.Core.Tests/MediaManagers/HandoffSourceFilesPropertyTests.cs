using FsCheck;
using FsCheck.Fluent;
using Weir.Core.MediaManagers;
using Weir.TestSupport;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// <see cref="HandoffSourceFiles"/> over lists of files written with detours (<c>.</c>, and a folder entered then left again), in
/// either kind of separator, with duplicates, and with files that are not in the folder the hand-off names.
/// </summary>
public sealed class HandoffSourceFilesPropertyTests
{
    private sealed record Listing(string SourcePath, string SourceRelativePath, IReadOnlyList<string> Listed, IReadOnlyList<string> Expected, bool Windows);

    private static readonly Gen<Listing> InsideTheFolder =
        from root in Gen.Elements(PathGenerators.Roots)
        from folderNames in PathGenerators.Names(1, 3)
        from libraryNames in PathGenerators.Names(1, 3)
        from files in Gen.ListOf(DetouredFileBelow(root, folderNames))
        from windows in Gen.Elements(true, false)
        from sourcePath in PathGenerators.Render(root, folderNames, varyCase: false, trailingSeparator: true)
        let relative = string.Join('/', libraryNames)
        select new Listing(
            sourcePath,
            relative,
            [.. files.Select(file => file.Text)],
            DistinctInOrder([.. files.Select(file => string.Join('/', new[] { relative }.Concat(file.Below)))], windows),
            windows);

    [Fact]
    public void Every_listed_file_below_the_folder_is_kept_once_in_the_order_it_was_first_listed()
    {
        PropertyChecks.ForAll(InsideTheFolder, listing =>
        {
            var result = HandoffSourceFiles.Resolve(listing.SourcePath, listing.SourceRelativePath, listing.Listed, listing.Windows);
            Assert.True(result.Ok, result.Problem);
            Assert.Equal(listing.Expected, result.RelativePaths);
        });
    }

    [Fact]
    public void One_file_outside_the_folder_refuses_the_whole_list_wherever_it_is_in_it()
    {
        var outside =
            from listing in InsideTheFolder
            from other in PathGenerators.Name
            from stray in Gen.Elements(
                listing.SourcePath + "/..",
                listing.SourcePath + "/../../film.mkv",
                listing.SourcePath + "/../zz" + other + "/film.mkv",
                "/zz" + other + "/film.mkv")
            from position in Gen.Choose(0, listing.Listed.Count)
            select (listing, stray, position);

        PropertyChecks.ForAll(outside, found =>
        {
            var (listing, stray, position) = found;
            var listed = new List<string>(listing.Listed);
            listed.Insert(position, stray);
            var result = HandoffSourceFiles.Resolve(listing.SourcePath, listing.SourceRelativePath, listed, listing.Windows);
            Assert.False(result.Ok, string.Join(" | ", result.RelativePaths ?? []));
            Assert.Contains("not inside the folder", result.Problem, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_folder_written_in_another_case_is_the_same_folder_only_where_case_does_not_matter()
    {
        var recased =
            from root in Gen.Elements(PathGenerators.Roots)
            from folderNames in PathGenerators.Names(1, 3).Where(names => names.Any(name => !string.Equals(name.ToUpperInvariant(), name, StringComparison.Ordinal)))
            from fileName in PathGenerators.Name
            from sourcePath in PathGenerators.Render(root, folderNames, varyCase: false)
            select (sourcePath, file: root + string.Join('/', folderNames).ToUpperInvariant() + "/" + fileName);

        PropertyChecks.ForAll(recased, found =>
        {
            Assert.True(HandoffSourceFiles.Resolve(found.sourcePath, "lib", [found.file], windows: true).Ok);
            Assert.False(HandoffSourceFiles.Resolve(found.sourcePath, "lib", [found.file], windows: false).Ok);
        });
    }

    [Fact]
    public void Whatever_is_listed_the_answer_is_paths_below_the_folder_or_the_reason_there_are_none()
    {
        var soup = Gen.Choose(0, 14).SelectMany(length => Gen.ArrayOf(Gen.Elements("aB/\\.. :é".ToCharArray()), length)).Select(chars => new string(chars));
        var lists = Gen.Choose(0, 4).SelectMany(count => Gen.ArrayOf(soup, count));

        PropertyChecks.ForAll(soup, lists, (sourcePath, listed) =>
        {
            var result = HandoffSourceFiles.Resolve(sourcePath, "lib/film", listed, windows: false);
            if (!result.Ok)
            {
                Assert.False(string.IsNullOrEmpty(result.Problem));
                Assert.False(result.Problem!.Contains('/', StringComparison.Ordinal));
                return;
            }

            Assert.True(result.RelativePaths!.Count <= listed.Length);
            Assert.Equal(result.RelativePaths.Count, result.RelativePaths.Distinct(StringComparer.Ordinal).Count());
            Assert.All(result.RelativePaths, path =>
            {
                Assert.True(path == "lib/film" || path.StartsWith("lib/film/", StringComparison.Ordinal), path);
                Assert.DoesNotContain(path.Split('/'), part => part is "" or "." or "..");
            });
        });
    }

    /// <summary>A file below <paramref name="folderNames"/>, written with detours that cancel, and the names it has below the folder.</summary>
    private static Gen<ListedFile> DetouredFileBelow(string root, IReadOnlyList<string> folderNames) =>
        from below in PathGenerators.Names(0, 3)
        from name in PathGenerators.Name
        from detours in Gen.ArrayOf(Gen.Elements(Detour.None, Detour.Dot, Detour.EnterAndLeave), below.Count + 1)
        from rootText in Gen.Elements(root, root.Replace('\\', '/'), root.Replace('/', '\\'))
        from detourName in PathGenerators.Name
        let parts = WithDetours([.. below, name], detours, detourName)
        from text in PathGenerators.Render(rootText, [.. folderNames, .. parts], varyCase: false)
        select new ListedFile(text, [.. below, name]);

    private static List<string> WithDetours(IReadOnlyList<string> names, Detour[] detours, string detourName)
    {
        var parts = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            switch (detours[i])
            {
                case Detour.Dot:
                    parts.Add(".");
                    break;
                case Detour.EnterAndLeave:
                    parts.Add(detourName);
                    parts.Add("..");
                    break;
            }

            parts.Add(names[i]);
        }

        return parts;
    }

    private static List<string> DistinctInOrder(IEnumerable<string> paths, bool windows) =>
        [.. paths.Distinct(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];

    private enum Detour
    {
        None,
        Dot,
        EnterAndLeave,
    }

    private sealed record ListedFile(string Text, IReadOnlyList<string> Below);
}
