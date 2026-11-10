using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>How a path under Weir's output folder is written under the folder the manager sees it as.</summary>
public sealed class HandoffOutputPathTranslationTests
{
    private static readonly string Root = Path.GetTempPath();

    [Fact]
    public void A_path_below_the_folder_is_rebuilt_below_the_managers_folder()
    {
        var translated = HandoffCompletionReporter.TranslateOutputPath(Path.Join(Root, "Refined", "Film", "film.mkv"), Path.Join(Root, "Refined"), "/mnt/out");

        Assert.Equal("/mnt/out/Film/film.mkv", translated);
    }

    [Fact]
    public void A_folder_written_with_a_closing_separator_is_the_same_folder()
    {
        var folder = Path.Join(Root, "Refined") + Path.DirectorySeparatorChar;

        var translated = HandoffCompletionReporter.TranslateOutputPath(Path.Join(Root, "Refined", "Film", "film.mkv"), folder, "/mnt/out");

        Assert.Equal("/mnt/out/Film/film.mkv", translated);
    }

    [Fact]
    public void A_path_that_is_the_folder_itself_or_outside_it_is_not_translated()
    {
        Assert.Null(HandoffCompletionReporter.TranslateOutputPath(Path.Join(Root, "Refined"), Path.Join(Root, "Refined"), "/mnt/out"));
        Assert.Null(HandoffCompletionReporter.TranslateOutputPath(Path.Join(Root, "Elsewhere", "film.mkv"), Path.Join(Root, "Refined"), "/mnt/out"));
    }

    [Fact]
    public void A_folder_named_with_other_capitals_is_the_same_folder_only_where_paths_ignore_case()
    {
        var translated = HandoffCompletionReporter.TranslateOutputPath(Path.Join(Root, "REFINED", "Film", "film.mkv"), Path.Join(Root, "Refined"), "/mnt/out");

        Assert.Equal(OperatingSystem.IsWindows() ? "/mnt/out/Film/film.mkv" : null, translated);
    }
}
