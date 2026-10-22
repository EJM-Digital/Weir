using Weir.Core.MediaManagers;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Core.Tests.Processing.RemuxPass;

/// <summary>When the gate cannot resolve a path, finds the release elsewhere, or lists more paths than it shows.</summary>
public sealed class LibraryTruthGateEdgeTests
{
    private static readonly ManagerConnection Main = new("deluno", "Main", "http://m", "k");

    private static ManagerLibraryTruth Reported(params string[] paths) => new(Main, SignalStatus.Reported, paths);

    private static LibraryTruthVerdict Evaluate(
        IReadOnlyList<ManagerLibraryTruth> answers,
        Func<string, string?>? resolve = null,
        bool ignoreCase = false,
        string? expectedOutputFile = null,
        bool acknowledged = false) =>
        LibraryTruthGate.EvaluateForFolder(answers, "/lib/Film", "movie", resolve ?? (path => path), ignoreCase, expectedOutputFile, acknowledged);

    [Fact]
    public void A_folder_that_cannot_be_resolved_is_compared_as_written()
    {
        var verdict = Evaluate([Reported("/lib/Film/film.mkv")], path => path == "/lib/Film" ? null : path);

        Assert.Equal(LibraryTruthVerdict.Failed, verdict.Check);
    }

    [Fact]
    public void A_library_path_that_cannot_be_resolved_is_not_inside_anything()
    {
        var verdict = Evaluate([Reported("/lib/Film/film.mkv")], path => path == "/lib/Film/film.mkv" ? null : path);

        Assert.Equal(LibraryTruthVerdict.Passed, verdict.Check);
    }

    [Fact]
    public void More_than_three_library_files_are_summarised_in_the_note()
    {
        var verdict = Evaluate([Reported("/lib/Film/a.mkv", "/lib/Film/b.mkv", "/lib/Film/c.mkv", "/lib/Film/d.mkv", "/lib/Film/e.mkv")]);

        Assert.Equal(LibraryTruthVerdict.Failed, verdict.Check);
        Assert.Contains("(+2 more)", verdict.Note, StringComparison.Ordinal);
        Assert.Contains("Example paths:", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_library_file_is_an_example_path_in_the_singular()
    {
        var verdict = Evaluate([Reported("/lib/Film/a.mkv")]);

        Assert.Contains("Example path:", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_silent_manager_with_no_detail_adds_nothing_to_the_note()
    {
        var verdict = Evaluate([new ManagerLibraryTruth(Main, SignalStatus.Unreachable, [])]);

        Assert.Equal(LibraryTruthVerdict.Skipped, verdict.Check);
        Assert.EndsWith("so it was left in place.", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_reported_at_the_exact_output_path_confirms_the_import()
    {
        var verdict = Evaluate([Reported("/other/Film/film.mkv")], expectedOutputFile: "/other/Film/film.mkv");

        Assert.Equal(LibraryTruthVerdict.Passed, verdict.Check);
    }

    [Fact]
    public void A_release_reported_under_the_same_title_elsewhere_confirms_the_import()
    {
        var verdict = Evaluate([Reported("/other/Renamed/FILM.mkv")], expectedOutputFile: "/lib/Film/film.mkv");

        Assert.Equal(LibraryTruthVerdict.Passed, verdict.Check);
    }

    [Fact]
    public void An_unrelated_library_file_does_not_confirm_the_import()
    {
        var verdict = Evaluate([Reported("/other/Else/else.mkv")], expectedOutputFile: "/lib/Film/film.mkv");

        Assert.Equal(LibraryTruthVerdict.Skipped, verdict.Check);
    }

    [Fact]
    public void Unresolvable_paths_are_compared_as_written_when_looking_for_the_import()
    {
        var verdict = Evaluate([Reported("/other/Film/film.mkv")], _ => null, expectedOutputFile: "/other/Film/film.mkv");

        Assert.Equal(LibraryTruthVerdict.Passed, verdict.Check);
    }

    [Fact]
    public void An_acknowledged_hand_off_needs_no_further_evidence()
    {
        var verdict = Evaluate([Reported()], expectedOutputFile: "/lib/Film/film.mkv", acknowledged: true);

        Assert.True(verdict.ClearsDelete);
    }

    [Theory]
    [InlineData("/a/b/c", "/a/b", false, true)]
    [InlineData("/a/b", "/a/b", false, true)]
    [InlineData("/a", "/a/b", false, false)]
    [InlineData("/a/x", "/a/b", false, false)]
    [InlineData("/A/B/c", "/a/b", true, true)]
    [InlineData("/A/B/c", "/a/b", false, false)]
    [InlineData(@"C:\Lib\Film", @"C:\Lib", true, true)]
    [InlineData("a/b", "/a/b", false, false)]
    public void A_path_is_under_a_root_segment_by_segment(string path, string root, bool ignoreCase, bool expected) =>
        Assert.Equal(expected, LibraryTruthGate.IsSameOrUnder(path, root, ignoreCase));
}
