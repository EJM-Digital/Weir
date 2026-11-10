using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>A hand-off's <c>sourceFiles</c>: how Deluno's dialect reads the list, and which listed paths are inside the folder it names.</summary>
public sealed class HandoffSourceFilesTests
{
    private static MediaManagerImportEvent Deluno(string sourceFiles) =>
        ImportEvents.DialectForSource("deluno")!.Normalize((WireObject)WireJsonParser.Parse(
            $$"""{"eventType":"deluno.processor-handoff","handoffId":"h1","mediaType":"movies","sourcePath":"/w/Film"{{sourceFiles}} }"""))!;

    [Fact]
    public void The_list_is_read_as_given_with_each_path_stripped()
    {
        Assert.Equal(["/w/Film/film.mkv", "/w/Film/extras/b.mkv"], Deluno(""","sourceFiles":[" /w/Film/film.mkv ","/w/Film/extras/b.mkv"]""").SourceFiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData(""","sourceFiles":null""")]
    public void No_list_is_no_list(string sourceFiles) => Assert.Null(Deluno(sourceFiles).SourceFiles);

    [Fact]
    public void An_empty_list_is_kept_empty_and_means_the_whole_folder()
    {
        Assert.Empty(Deluno(""","sourceFiles":[]""").SourceFiles!);
    }

    [Theory]
    [InlineData(""","sourceFiles":[7]""")]
    [InlineData(""","sourceFiles":["/w/Film/film.mkv",null,"  "]""")]
    [InlineData(",\"sourceFiles\":\"/w/Film/film.mkv\"")]
    public void Anything_that_is_not_a_path_is_kept_as_an_empty_one_so_intake_refuses_it(string sourceFiles) =>
        Assert.Contains(string.Empty, Deluno(sourceFiles).SourceFiles!);

    [Fact]
    public void Another_dialect_does_not_read_the_list()
    {
        var native = ImportEvents.DialectForSource("native")!.Normalize((WireObject)WireJsonParser.Parse(
            """{"event":"handoff","media_scope":"movie","file_path":"/w/Film","sourceFiles":["/w/Film/film.mkv"]}"""))!;
        Assert.Null(native.SourceFiles);
    }

    [Theory]
    [InlineData("/w/Film", "Film", new[] { "/w/Film/film.mkv" }, false, new[] { "Film/film.mkv" })]
    [InlineData("/w/Film/", "Film", new[] { "/w/Film/a/b.mkv", "/w//Film/./c.mkv" }, false, new[] { "Film/a/b.mkv", "Film/c.mkv" })]
    [InlineData("/w/Film", "Film", new[] { "/w/Film/sub/../film.mkv" }, false, new[] { "Film/film.mkv" })]
    [InlineData("/w/Film", "Film", new[] { "/w/Film/film.mkv", "/w/Film/./film.mkv" }, false, new[] { "Film/film.mkv" })]
    [InlineData("/w/Film", "Film", new[] { "/w/Film" }, false, new[] { "Film" })]
    [InlineData(@"D:\Downloads\Film", "Film", new[] { @"D:\Downloads\Film\film.mkv" }, true, new[] { "Film/film.mkv" })]
    [InlineData(@"D:\Downloads\Film", "Film", new[] { @"d:\downloads\FILM\Film.mkv" }, true, new[] { "Film/Film.mkv" })]
    [InlineData("/w/Show", "tv/Show", new[] { "/w/Show/S01/e1.mkv" }, false, new[] { "tv/Show/S01/e1.mkv" })]
    public void A_file_at_or_below_the_folder_resolves_to_its_path_in_the_library(
        string sourcePath, string sourceRelativePath, string[] listed, bool windows, string[] expected)
    {
        var result = HandoffSourceFiles.Resolve(sourcePath, sourceRelativePath, listed, windows);

        Assert.True(result.Ok);
        Assert.Equal(expected, result.RelativePaths);
        Assert.Null(result.Problem);
    }

    [Theory]
    [InlineData("/w/Film", "/w/Other/other.mkv", false)]
    [InlineData("/w/Film", "/w/Film-extras/film.mkv", false)]
    [InlineData("/w/Film", "/w/film/film.mkv", false)]
    [InlineData("/w/Film", "/w", false)]
    [InlineData("/w/Film", "/w/Film/../Other/other.mkv", false)]
    [InlineData("/w/Film", "/w/Film/sub/../../Other/other.mkv", false)]
    [InlineData("/w/Film", "/../../etc/other.mkv", false)]
    [InlineData("/w/Film", "/../w/Film/film.mkv", false)]    [InlineData("/w/Film", "film.mkv", false)]
    [InlineData(@"D:\Downloads\Film", @"D:\Downloads\Film\..\Other\other.mkv", true)]
    [InlineData(@"D:\Downloads\Film", @"E:\Downloads\Film\other.mkv", true)]
    public void A_file_outside_the_folder_refuses_the_list_naming_only_the_file(string sourcePath, string outside, bool windows)
    {
        var result = HandoffSourceFiles.Resolve(sourcePath, "Film", [sourcePath + "/film.mkv", outside], windows);

        Assert.False(result.Ok);
        Assert.Null(result.RelativePaths);
        Assert.Equal(HandoffSourceFiles.OutsideDetail(outside), result.Problem);
        Assert.DoesNotContain("Downloads", result.Problem, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was queued.", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_whose_name_differs_only_in_case_is_outside_on_a_case_sensitive_system()
    {
        Assert.False(HandoffSourceFiles.Resolve("/w/Film", "Film", ["/w/FILM/film.mkv"], windows: false).Ok);
        Assert.True(HandoffSourceFiles.Resolve("/w/Film", "Film", ["/w/FILM/film.mkv"], windows: true).Ok);
    }

    [Fact]
    public void An_entry_with_no_path_refuses_the_list()
    {
        var result = HandoffSourceFiles.Resolve("/w/Film", "Film", ["/w/Film/film.mkv", " "], windows: false);

        Assert.False(result.Ok);
        Assert.Equal("The hand-off lists a file with no path. Nothing was queued.", result.Problem);
    }

    [Fact]
    public void The_missing_file_message_names_only_the_file()
    {
        Assert.Equal(
            "The hand-off lists 'gone.mkv', but Weir cannot find that file. " +
            "Point the media manager and Weir at the same folder — both hosts have to see it at that path. Nothing was queued.",
            HandoffSourceFiles.MissingDetail("Film/gone.mkv"));
    }
}
