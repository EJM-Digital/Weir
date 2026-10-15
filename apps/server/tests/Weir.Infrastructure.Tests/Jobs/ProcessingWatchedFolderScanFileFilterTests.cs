namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// Which files in a watched folder a scan looks at, as the workflow's saved settings decide it: the file types it takes, the
/// downloader folders it ignores, hidden files and folders, and the top folder only. Real folders, real SQLite.
/// </summary>
public sealed class ProcessingWatchedFolderScanFileFilterTests
{
    [Fact]
    public async Task A_workflow_with_a_list_of_file_types_takes_only_those_types()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { MediaExtensionsCsv = ".mkv, ts" });
        world.Write("Listed.mkv");
        world.Write("Listed Too.TS");
        world.Write("Unlisted.mp4");

        await world.ScanAsync(workflow);

        Assert.Equal(["Listed Too.TS", "Listed.mkv"], await world.QueuedPathsAsync());
        Assert.Null(await world.RowAsync(workflow, "Unlisted.mp4"));
    }

    [Fact]
    public async Task A_workflow_with_no_list_takes_the_built_in_video_types()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync();
        world.Write("Built In.mp4");
        world.Write("Not Built In.ts");

        await world.ScanAsync(workflow);

        Assert.Equal(["Built In.mp4"], await world.QueuedPathsAsync());
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("_UNPACK_")]
    [InlineData(".sabnzbd")]
    [InlineData("__admin__")]
    public async Task A_file_in_a_downloaders_unfinished_folder_is_left_out_when_the_workflow_names_no_folders(string marker)
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { ExcludeHidden = false });
        world.Write($"{marker}/Still Downloading.mkv");
        world.Write("Finished/Done.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal(["Finished/Done.mkv"], await world.QueuedPathsAsync());
        Assert.Null(await world.RowAsync(workflow, $"{marker}/Still Downloading.mkv"));
    }

    [Fact]
    public async Task A_file_in_a_folder_the_workflow_names_is_left_out()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { ExcludeMarkersCsv = "Downloading, torrent-temp" });
        world.Write("Downloading/Half.mkv");
        world.Write("Release/torrent-temp/Half Too.mkv");
        world.Write("Release/Whole.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal(["Release/Whole.mkv"], await world.QueuedPathsAsync());
    }

    [Fact]
    public async Task The_folders_a_workflow_names_replace_the_built_in_ones()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { ExcludeMarkersCsv = "downloading" });
        world.Write("incomplete/Taken.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal(["incomplete/Taken.mkv"], await world.QueuedPathsAsync());
    }

    [Fact]
    public async Task Hidden_files_and_files_in_hidden_folders_are_left_out_by_default()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync();
        world.Write(".Hidden File.mkv");
        world.Write("._Mac Fork.mkv");
        world.Write(".hidden-folder/Inside.mkv");
        world.Write("Shown.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal(["Shown.mkv"], await world.QueuedPathsAsync());
        Assert.Null(await world.RowAsync(workflow, ".hidden-folder/Inside.mkv"));
    }

    [Fact]
    public async Task A_workflow_that_keeps_hidden_files_takes_them()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { ExcludeHidden = false });
        world.Write(".Hidden File.mkv");
        world.Write(".hidden-folder/Inside.mkv");
        world.Write("Shown.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal([".Hidden File.mkv", ".hidden-folder/Inside.mkv", "Shown.mkv"], await world.QueuedPathsAsync());
    }

    [Fact]
    public async Task A_workflow_that_looks_at_the_top_folder_only_leaves_the_folders_below_alone()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { TopLevelOnly = true });
        world.Write("Top.mkv");
        world.Write("Release/Below.mkv");
        world.Write("Release/Deeper/Deepest.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal(["Top.mkv"], await world.QueuedPathsAsync());
        Assert.Null(await world.RowAsync(workflow, "Release/Below.mkv"));
    }

    [Fact]
    public async Task A_workflow_that_looks_below_the_top_folder_takes_every_folder()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { TopLevelOnly = false });
        world.Write("Top.mkv");
        world.Write("Release/Below.mkv");
        world.Write("Release/Deeper/Deepest.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal(["Release/Below.mkv", "Release/Deeper/Deepest.mkv", "Top.mkv"], await world.QueuedPathsAsync());
    }

    /// <summary>A movie whose output is complete but whose original is still in its release folder, beside another video of the given name.</summary>
    private static async Task<(long Workflow, string Film, string Other)> InterruptedRemovalAsync(ScanWorkflowWorld world, string mediaExtensionsCsv, string otherName)
    {
        var workflow = await world.WorkflowAsync(w => w with { MediaExtensionsCsv = mediaExtensionsCsv, RemoveOriginalAfterSuccess = true });
        var film = world.Write("Film.2001/film.mkv", bytes: 8);
        var other = world.Write($"Film.2001/{otherName}", bytes: 8);
        var output = Path.Combine(world.Output, "Film.2001", "film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllBytesAsync(output, new byte[8]);
        await ProcessingWatchedFolderScanDispatchJobHandlerTests.WriteCompletedPassEventAsync(world.Store, workflow, "Film.2001/film.mkv", "movie", film, output);
        return (workflow, film, other);
    }

    [Fact]
    public async Task Finishing_a_movies_removal_keeps_a_release_folder_that_holds_another_video_of_a_type_the_workflow_names()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var (workflow, film, other) = await InterruptedRemovalAsync(world, "mkv, ts", "Cut.ts");

        await world.ScanAsync(workflow);

        Assert.False(File.Exists(film));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task Finishing_a_movies_removal_removes_the_whole_release_folder_when_the_other_file_is_not_a_video_the_workflow_takes()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var (workflow, film, other) = await InterruptedRemovalAsync(world, string.Empty, "Cut.ts");

        await world.ScanAsync(workflow);

        Assert.False(File.Exists(film));
        Assert.False(File.Exists(other));
        Assert.False(Directory.Exists(Path.GetDirectoryName(film)));
    }
}
