using Weir.Core.LibraryMode;
using Weir.Core.Processing;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

/// <summary>
/// Which files a library-folder walk counts, as the workflow's saved settings decide it, on a real folder: the same file types,
/// downloader folders, hidden files and top-folder rule that a watched-folder scan applies.
/// </summary>
public sealed class LibraryFileWalkerFilterTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private void WriteFile(params string[] relativeParts)
    {
        var path = _folder.Join(relativeParts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    private IReadOnlyList<string> Walk(ProcessingLibraryRecord workflow) =>
        [.. LibraryFileWalker.Walk(workflow, new LibrarySettings([_folder.Path], ScheduleEnabled: false))
            .Select(file => Path.GetRelativePath(_folder.Path, file.Path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    private static ProcessingLibraryRecord Workflow(Func<ProcessingLibraryRecord, ProcessingLibraryRecord>? settings = null)
    {
        var workflow = new ProcessingLibraryRecord { Name = "Movies" };
        return settings is null ? workflow : settings(workflow);
    }

    [Fact]
    public void A_workflow_with_a_list_of_file_types_walks_only_those_types()
    {
        WriteFile("Film.mkv");
        WriteFile("Film.TS");
        WriteFile("Film.mp4");

        var walked = Walk(Workflow(w => w with { MediaExtensionsCsv = ".mkv, ts" }));

        Assert.Equal(["Film.TS", "Film.mkv"], walked);
    }

    [Fact]
    public void A_workflow_with_no_list_walks_the_built_in_video_types_and_not_the_files_beside_them()
    {
        WriteFile("Film.mkv");
        WriteFile("Film.avi");
        WriteFile("Film.nfo");
        WriteFile("poster.jpg");
        WriteFile("Film.en.srt");

        var walked = Walk(Workflow());

        Assert.Equal(["Film.avi", "Film.mkv"], walked);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("_UNPACK_")]
    [InlineData("__admin__")]
    public void A_file_in_a_downloaders_unfinished_folder_is_not_walked_when_the_workflow_names_no_folders(string marker)
    {
        WriteFile(marker, "Half.mkv");
        WriteFile("Whole", "Film.mkv");

        Assert.Equal(["Whole/Film.mkv"], Walk(Workflow()));
    }

    [Fact]
    public void A_file_in_a_folder_the_workflow_names_is_not_walked()
    {
        WriteFile("Downloading", "Half.mkv");
        WriteFile("Release", "torrent-temp", "Half Too.mkv");
        WriteFile("Release", "Whole.mkv");

        var walked = Walk(Workflow(w => w with { ExcludeMarkersCsv = "downloading, Torrent-Temp" }));

        Assert.Equal(["Release/Whole.mkv"], walked);
    }

    [Fact]
    public void A_film_whose_title_holds_a_folder_name_is_still_walked()
    {
        WriteFile("The Incomplete Man.mkv");

        Assert.Equal(["The Incomplete Man.mkv"], Walk(Workflow(w => w with { ExcludeMarkersCsv = "incomplete" })));
    }

    [Fact]
    public void The_folders_a_workflow_names_replace_the_built_in_ones()
    {
        WriteFile("incomplete", "Taken.mkv");

        Assert.Equal(["incomplete/Taken.mkv"], Walk(Workflow(w => w with { ExcludeMarkersCsv = "downloading" })));
    }

    [Fact]
    public void Hidden_files_and_files_in_hidden_folders_are_not_walked_by_default()
    {
        WriteFile(".Hidden.mkv");
        WriteFile("._Mac Fork.mkv");
        WriteFile(".hidden-folder", "Inside.mkv");
        WriteFile("Shown.mkv");

        Assert.Equal(["Shown.mkv"], Walk(Workflow()));
    }

    [Fact]
    public void A_workflow_that_keeps_hidden_files_walks_them()
    {
        WriteFile(".Hidden.mkv");
        WriteFile(".hidden-folder", "Inside.mkv");
        WriteFile("Shown.mkv");

        var walked = Walk(Workflow(w => w with { ExcludeHidden = false }));

        Assert.Equal([".Hidden.mkv", ".hidden-folder/Inside.mkv", "Shown.mkv"], walked);
    }

    [Fact]
    public void A_library_folder_inside_a_hidden_folder_is_still_walked()
    {
        var hiddenRoot = new TempDirectory();
        try
        {
            var path = Path.Join(hiddenRoot.Path, ".media", "Film.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1]);

            var walked = LibraryFileWalker.Walk(Workflow(), new LibrarySettings([Path.Join(hiddenRoot.Path, ".media")], ScheduleEnabled: false));

            Assert.Single(walked);
        }
        finally
        {
            hiddenRoot.Dispose();
        }
    }

    [Fact]
    public void A_workflow_that_looks_at_the_top_folder_only_walks_nothing_below_it()
    {
        WriteFile("Top.mkv");
        WriteFile("Release", "Below.mkv");

        Assert.Equal(["Top.mkv"], Walk(Workflow(w => w with { TopLevelOnly = true })));
    }

    [Fact]
    public void A_workflow_that_looks_below_the_top_folder_walks_every_folder()
    {
        WriteFile("Top.mkv");
        WriteFile("Release", "Below.mkv");

        Assert.Equal(["Release/Below.mkv", "Top.mkv"], Walk(Workflow(w => w with { TopLevelOnly = false })));
    }
}
