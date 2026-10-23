using Weir.Core.Json;
using Weir.Infrastructure.Processing;

namespace Weir.Infrastructure.Tests.Processing;

/// <summary>Small readings of a pass's report and of a file's path that the larger suites do not reach.</summary>
public sealed class ProcessingSmallEdgeTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("/", "/")]
    [InlineData("Film (2020).mkv", "Film (2020)")]
    [InlineData("Film", "Film")]
    [InlineData("Pack\\Film (2020)\\Film.mkv", "Film (2020)")]
    public void A_release_is_titled_by_its_folder_or_by_the_file_name_at_the_root(string relativePath, string title)
    {
        Assert.Equal(title, new HoldDiagnosticStore(new LibraryStore()).ReleaseTitleFromRelativePath(relativePath));
    }

    [Fact]
    public void A_report_that_gives_its_numbers_as_words_or_flags_is_read_as_numbers()
    {
        var progress = LiveProgress.FromReport(new WireObject().Set("percent", "42.5").Set("eta_seconds", true).Set("elapsed_seconds", "soon"));

        Assert.Equal(42.5, progress.Percent);
        Assert.Equal(1.0, progress.EtaSeconds);
        Assert.Null(progress.ElapsedSeconds);
    }

    [Fact]
    public void Removing_a_blank_path_from_the_live_progress_changes_nothing()
    {
        var store = new LiveProgressStore();
        store.Update("Film.mkv", LiveProgress.FromReport(new WireObject().Set("percent", 10)));

        store.Remove(" ");

        Assert.Single(store.Snapshot());
    }
}
