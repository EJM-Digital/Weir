using FsCheck;
using FsCheck.Fluent;
using Weir.Infrastructure.MediaManagers;
using Weir.TestSupport;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// <see cref="HandoffCompletionReporter.TranslateOutputPath"/>: an output file under Weir's output folder, named to a media manager as
/// the same file under the manager's own folder, whichever kind of path (Windows, POSIX, a share) its host uses.
/// </summary>
public sealed class HandoffOutputPathPropertyTests : IDisposable
{
    private static readonly string[] ManagerRoots = ["/", "C:\\", "d:/", "\\\\nas\\media\\", "//nas/media/", "/mnt/"];

    private static readonly char[] Separators = ['/', '\\'];

    private readonly TempDirectory _output = new();

    private sealed record Translation(string Local, string Manager, string File, IReadOnlyList<string> Below);

    private Gen<Translation> Inside =>
        from localNames in PathGenerators.Names(0, 2)
        from below in PathGenerators.Names(1, 3)
        from managerRoot in Gen.Elements(ManagerRoots)
        from managerNames in PathGenerators.Names(0, 3)
        from manager in PathGenerators.Render(managerRoot, managerNames, varyCase: false, trailingSeparator: true)
        let local = Path.Join([_output.Path, .. localNames])
        select new Translation(local + Path.DirectorySeparatorChar, manager, Path.Join([local, .. below]), below);

    public void Dispose() => _output.Dispose();

    [Fact]
    public void A_file_below_the_output_folder_is_the_same_place_below_the_managers_folder_in_the_managers_own_style()
    {
        PropertyChecks.ForAll(Inside, found =>
        {
            var translated = HandoffCompletionReporter.TranslateOutputPath(found.File, found.Local, found.Manager);

            Assert.NotNull(translated);
            Assert.Equal([.. found.Manager.Split(Separators, StringSplitOptions.RemoveEmptyEntries), .. found.Below], translated.Split(Separators, StringSplitOptions.RemoveEmptyEntries));
            var windows = found.Manager.Contains('\\', StringComparison.Ordinal) || found.Manager.Contains(':', StringComparison.Ordinal);
            Assert.False(translated.Contains(windows ? '/' : '\\', StringComparison.Ordinal), translated);
        });
    }

    [Fact]
    public void The_output_folder_itself_a_folder_that_only_shares_its_name_and_a_file_elsewhere_are_not_translated()
    {
        var elsewhere =
            from found in Inside
            from extra in PathGenerators.Name
            from other in Gen.Elements(Other.Folder, Other.LookAlike, Other.AboveIt)
            select (found, extra, other);

        PropertyChecks.ForAll(elsewhere, found =>
        {
            var folder = found.found.Local.TrimEnd(Path.DirectorySeparatorChar);
            var file = found.other switch
            {
                Other.Folder => folder,
                Other.LookAlike => Path.Join(folder + found.extra, "film.mkv"),
                _ => Path.Join(Path.GetDirectoryName(folder), "film.mkv"),
            };

            Assert.Null(HandoffCompletionReporter.TranslateOutputPath(file, found.found.Local, found.found.Manager));
        });
    }

    [Fact]
    public void A_path_the_system_cannot_use_gives_no_translation_instead_of_an_error()
    {
        var unusable = Gen.Elements("a\0b", "\0", "x\0/film.mkv");
        PropertyChecks.ForAll(unusable, file =>
        {
            Assert.Null(HandoffCompletionReporter.TranslateOutputPath(file, _output.Path, "/out"));
            Assert.Null(HandoffCompletionReporter.TranslateOutputPath(Path.Join(_output.Path, "film.mkv"), file, "/out"));
        });
    }

    private enum Other
    {
        Folder,
        LookAlike,
        AboveIt,
    }
}
