using Weir.Core.Processing;
using Weir.Core.Time;

namespace Weir.Infrastructure.Tests.Jobs;

/// <summary>
/// What a scan does with a file its workflow's intake rules turn away: the file is recorded as skipped with the reason and no
/// pass is queued. Covers the date windows at their boundary second, the path and size rules, and the readability check.
/// Real folders, real SQLite.
/// </summary>
public sealed class ProcessingWatchedFolderScanAdmissionTests
{
    private const string Film = "Film 2001.mkv";

    private static readonly DateTime FixedTime = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A window edge <paramref name="offsetSeconds"/> from the file's own time. Created times are read back from the disk, which
    /// keeps the boundary exact on a filesystem that does not take a creation time to be set.
    /// </summary>
    private static ProcessingLibraryInput WithEdge(ProcessingLibraryInput workflow, string setting, DateTime fileTimeUtc, int offsetSeconds)
    {
        var edge = Timestamp.FromUtc(Timestamp.TruncateToMicroseconds(fileTimeUtc).AddSeconds(offsetSeconds));
        return setting switch
        {
            "created_after" => workflow with { CreatedAfter = edge },
            "created_before" => workflow with { CreatedBefore = edge },
            "modified_after" => workflow with { ModifiedAfter = edge },
            "modified_before" => workflow with { ModifiedBefore = edge },
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
        };
    }

    [Theory]
    [InlineData("created_after", 0, true)]
    [InlineData("created_after", 1, false)]
    [InlineData("created_before", 0, false)]
    [InlineData("created_before", 1, true)]
    [InlineData("modified_after", 0, true)]
    [InlineData("modified_after", 1, false)]
    [InlineData("modified_before", 0, false)]
    [InlineData("modified_before", 1, true)]
    public async Task A_date_window_decides_whether_a_scan_queues_the_file(string setting, int edgeOffsetSeconds, bool queued)
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var path = world.Write(Film);
        File.SetCreationTimeUtc(path, FixedTime);
        File.SetLastWriteTimeUtc(path, FixedTime);
        var fileTime = setting.StartsWith("created", StringComparison.Ordinal) ? File.GetCreationTimeUtc(path) : File.GetLastWriteTimeUtc(path);
        var workflow = await world.WorkflowAsync(w => WithEdge(w, setting, fileTime, edgeOffsetSeconds));

        await world.ScanAsync(workflow);

        var row = await world.RowAsync(workflow, Film);
        Assert.NotNull(row);
        if (queued)
        {
            Assert.Equal([Film], await world.QueuedPathsAsync());
            Assert.Equal(ProcessingFileStatuses.Unprocessed, row.Status);
            return;
        }

        Assert.Empty(await world.QueuedPathsAsync());
        Assert.Equal(ProcessingFileStatuses.Skipped, row.Status);
        Assert.Contains("allowed window", row.StatusReason, StringComparison.Ordinal);
        Assert.Contains(setting.StartsWith("created", StringComparison.Ordinal) ? "creation time" : "last-modified time", row.StatusReason, StringComparison.Ordinal);
        Assert.Null(row.SkipKind);
    }

    [Fact]
    public async Task A_file_matching_none_of_the_include_patterns_is_skipped_and_not_queued()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { IncludePatternsCsv = "*feature*, Movies/*" });
        world.Write("Release/Feature Cut.mkv");
        world.Write("Movies/Other.mkv");
        world.Write("Release/Bonus Disc.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal(["Movies/Other.mkv", "Release/Feature Cut.mkv"], await world.QueuedPathsAsync());
        var skipped = await world.RowAsync(workflow, "Release/Bonus Disc.mkv");
        Assert.Equal(ProcessingFileStatuses.Skipped, skipped!.Status);
        Assert.Contains("does not match this workflow's include patterns", skipped.StatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workflow_with_no_include_patterns_takes_every_path()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync();
        world.Write("Release/Bonus Disc.mkv");

        await world.ScanAsync(workflow);

        Assert.Equal(["Release/Bonus Disc.mkv"], await world.QueuedPathsAsync());
    }

    [Fact]
    public async Task A_file_over_the_maximum_size_is_skipped_for_the_person_to_decide_and_not_queued()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { MaxFileSizeMb = 1 });
        world.Write("Within.mkv", bytes: 1024 * 1024);
        world.Write("Over.mkv", bytes: (1024 * 1024) + 1);

        await world.ScanAsync(workflow);

        Assert.Equal(["Within.mkv"], await world.QueuedPathsAsync());
        var over = await world.RowAsync(workflow, "Over.mkv");
        Assert.Equal(ProcessingFileStatuses.Skipped, over!.Status);
        Assert.Contains("exceeds the 1 MB workflow maximum", over.StatusReason, StringComparison.Ordinal);
        Assert.Null(over.SkipKind);
    }

    [Fact]
    public async Task A_workflow_with_no_maximum_size_takes_a_file_of_any_size()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { MaxFileSizeMb = 0 });
        world.Write("Large.mkv", bytes: (4 * 1024 * 1024) + 1);

        await world.ScanAsync(workflow);

        Assert.Equal(["Large.mkv"], await world.QueuedPathsAsync());
    }

    [Fact]
    public async Task A_file_that_cannot_be_opened_waits_and_is_not_queued()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { SkipAccessTests = false });
        using var held = new FileStream(world.Write(Film), FileMode.Open, FileAccess.Read, FileShare.None);

        await world.ScanAsync(workflow);

        var row = await world.RowAsync(workflow, Film);
        Assert.Equal(ProcessingFileStatuses.OnHold, row!.Status);
        Assert.Contains("could not open this file for reading", row.StatusReason, StringComparison.Ordinal);
        Assert.Empty(await world.QueuedPathsAsync());
    }

    [Fact]
    public async Task A_workflow_that_skips_the_readability_check_queues_a_file_that_cannot_be_opened()
    {
        using var world = await ScanWorkflowWorld.StartAsync();
        var workflow = await world.WorkflowAsync(w => w with { SkipAccessTests = true });
        using var held = new FileStream(world.Write(Film), FileMode.Open, FileAccess.Read, FileShare.None);

        await world.ScanAsync(workflow);

        Assert.Equal(ProcessingFileStatuses.Unprocessed, (await world.RowAsync(workflow, Film))!.Status);
        Assert.Equal([Film], await world.QueuedPathsAsync());
    }
}
