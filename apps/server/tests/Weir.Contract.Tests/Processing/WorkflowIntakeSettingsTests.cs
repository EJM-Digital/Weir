using System.Globalization;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Jobs;

namespace Weir.Contract.Tests.Processing;

/// <summary>One server, with the quiet environment, shared by the tests of what a workflow's saved intake settings do to a scan.</summary>
public sealed class IntakeScenarioFixture : IAsyncLifetime
{
    internal Scenario Scenario { get; private set; } = null!;

    public async Task InitializeAsync() => Scenario = await Scenario.StartAsync();

    public async Task DisposeAsync() => await Scenario.DisposeAsync();
}

/// <summary>
/// A workflow's intake settings, saved through the API and judged by what a scan of its folder then does: the files it lists, the
/// ones it skips and says why, and the passes it queues. Each test has a workflow and a folder of its own.
/// </summary>
[ContractArea("processing")]
public sealed class WorkflowIntakeSettingsTests(IntakeScenarioFixture fixture) : IClassFixture<IntakeScenarioFixture>
{
    private static readonly DateTime FixedTime = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private Scenario Scenario => fixture.Scenario;

    /// <summary>A movie workflow with folders of its own under the scenario's root, taking any size, with the given settings saved on it.</summary>
    private async Task<(JsonObject Library, string Watched)> WorkflowAsync(string name, params (string Name, JsonNode? Value)[] settings)
    {
        var root = Path.Combine(Scenario.Root, name);
        var watched = Directory.CreateDirectory(Path.Combine(root, "watched")).FullName;
        var library = await Scenario.CreateLibraryAsync(
        [
            ("name", name),
            ("watched_folder", watched),
            ("work_folder", Directory.CreateDirectory(Path.Combine(root, "work")).FullName),
            ("output_folder", Directory.CreateDirectory(Path.Combine(root, "output")).FullName),
            .. settings,
        ]);
        return (library, watched);
    }

    private static string Write(string watched, string relativePath, int bytes = 1)
    {
        var path = Path.Combine(watched, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>Scans the workflow's folder, waiting for the scan job to finish, and queues the passes it finds when asked.</summary>
    private async Task ScanAsync(JsonObject library, bool queuePasses = false) =>
        await Scenario.WaitForJobFinishedAsync(await Scenario.EnqueueScanAsync(library, enqueueRemuxJobs: queuePasses));

    private async Task<string?> StatusAsync(JsonObject library, string relativePath) => (string?)(await Scenario.FileRowAsync(library, relativePath))?["status"];

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_file_types_a_workflow_names_decide_which_files_a_scan_lists()
    {
        var (library, watched) = await WorkflowAsync("types", ("media_extensions_csv", ".mkv, ts"));
        Write(watched, "Listed.mkv");
        Write(watched, "Listed Too.ts");
        Write(watched, "Unlisted.mp4");

        await ScanAsync(library);

        Assert.Equal("unprocessed", await StatusAsync(library, "Listed.mkv"));
        Assert.Equal("unprocessed", await StatusAsync(library, "Listed Too.ts"));
        Assert.Null(await StatusAsync(library, "Unlisted.mp4"));
    }

    [Fact]
    public async Task A_scan_queues_a_pass_for_each_file_the_workflow_takes_and_none_for_the_rest()
    {
        var (library, watched) = await WorkflowAsync("queued", ("media_extensions_csv", "mkv"));
        Write(watched, "Taken.mkv");
        Write(watched, "Left.mp4");

        await ScanAsync(library, queuePasses: true);

        var queued = (await JobsApi.AllJobsAsync(Scenario.Admin))
            .Where(job => (string)job["job_kind"]! == Scenario.RemuxKind)
            .Select(job => (string)((JsonObject)JsonNode.Parse((string)job["payload_json"]!)!)["relative_media_path"]!)
            .ToList();
        Assert.Contains("Taken.mkv", queued);
        Assert.DoesNotContain("Left.mp4", queued);
    }

    [Fact]
    public async Task A_workflow_that_names_no_downloader_folders_ignores_the_built_in_ones_and_a_named_folder_replaces_them()
    {
        var (builtIn, builtInWatched) = await WorkflowAsync("markers-built-in");
        var (named, namedWatched) = await WorkflowAsync("markers-named", ("exclude_markers_csv", "downloading"));
        foreach (var watched in new[] { builtInWatched, namedWatched })
        {
            Write(watched, "incomplete/Half.mkv");
            Write(watched, "downloading/Half Too.mkv");
        }

        await ScanAsync(builtIn);
        await ScanAsync(named);

        Assert.Null(await StatusAsync(builtIn, "incomplete/Half.mkv"));
        Assert.Equal("unprocessed", await StatusAsync(builtIn, "downloading/Half Too.mkv"));
        Assert.Equal("unprocessed", await StatusAsync(named, "incomplete/Half.mkv"));
        Assert.Null(await StatusAsync(named, "downloading/Half Too.mkv"));
    }

    [Fact]
    public async Task Hidden_files_are_left_out_unless_the_workflow_keeps_them()
    {
        var (skipping, skippingWatched) = await WorkflowAsync("hidden-skipped");
        var (keeping, keepingWatched) = await WorkflowAsync("hidden-kept", ("exclude_hidden", false));
        foreach (var watched in new[] { skippingWatched, keepingWatched })
        {
            Write(watched, ".Hidden.mkv");
            Write(watched, ".hidden-folder/Inside.mkv");
            Write(watched, "Shown.mkv");
        }

        await ScanAsync(skipping);
        await ScanAsync(keeping);

        Assert.Equal("unprocessed", await StatusAsync(skipping, "Shown.mkv"));
        Assert.Null(await StatusAsync(skipping, ".Hidden.mkv"));
        Assert.Null(await StatusAsync(skipping, ".hidden-folder/Inside.mkv"));
        Assert.Equal("unprocessed", await StatusAsync(keeping, ".Hidden.mkv"));
        Assert.Equal("unprocessed", await StatusAsync(keeping, ".hidden-folder/Inside.mkv"));
    }

    [Fact]
    public async Task A_workflow_that_looks_at_the_top_folder_only_lists_nothing_below_it()
    {
        var (top, topWatched) = await WorkflowAsync("top-only", ("top_level_only", true));
        var (deep, deepWatched) = await WorkflowAsync("top-and-below");
        foreach (var watched in new[] { topWatched, deepWatched })
        {
            Write(watched, "Top.mkv");
            Write(watched, "Release/Below.mkv");
        }

        await ScanAsync(top);
        await ScanAsync(deep);

        Assert.Equal("unprocessed", await StatusAsync(top, "Top.mkv"));
        Assert.Null(await StatusAsync(top, "Release/Below.mkv"));
        Assert.Equal("unprocessed", await StatusAsync(deep, "Release/Below.mkv"));
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
    public async Task A_date_window_decides_whether_a_file_is_taken_or_skipped_with_its_reason(string setting, int edgeOffsetSeconds, bool taken)
    {
        var name = $"{setting}-{edgeOffsetSeconds}";
        var watched = Directory.CreateDirectory(Path.Combine(Scenario.Root, name, "watched")).FullName;
        var file = Write(watched, "Film.mkv");
        File.SetCreationTimeUtc(file, FixedTime);
        File.SetLastWriteTimeUtc(file, FixedTime);
        var fileTime = setting.StartsWith("created", StringComparison.Ordinal) ? File.GetCreationTimeUtc(file) : File.GetLastWriteTimeUtc(file);
        var edge = new DateTime(fileTime.Ticks - (fileTime.Ticks % 10), DateTimeKind.Utc).AddSeconds(edgeOffsetSeconds);
        var (library, _) = await WorkflowAsync(name, (setting, Iso(edge)));

        await ScanAsync(library);

        var row = await Scenario.FileRowAsync(library, "Film.mkv");
        Assert.NotNull(row);
        if (taken)
        {
            Assert.Equal("unprocessed", (string)row["status"]!);
            return;
        }

        Assert.Equal("skipped", (string)row["status"]!);
        Assert.Contains("allowed window", (string)row["status_reason"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_matching_no_include_pattern_is_skipped_with_its_reason()
    {
        var (library, watched) = await WorkflowAsync("include", ("include_patterns_csv", "*feature*"));
        Write(watched, "Feature Cut.mkv");
        Write(watched, "Bonus Disc.mkv");

        await ScanAsync(library);

        Assert.Equal("unprocessed", await StatusAsync(library, "Feature Cut.mkv"));
        var skipped = await Scenario.FileRowAsync(library, "Bonus Disc.mkv");
        Assert.Equal("skipped", (string)skipped!["status"]!);
        Assert.Contains("include patterns", (string)skipped["status_reason"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_over_the_maximum_size_is_skipped_with_no_below_minimum_kind()
    {
        var (library, watched) = await WorkflowAsync("maximum", ("max_file_size_mb", 1));
        Write(watched, "Within.mkv", bytes: 1024 * 1024);
        Write(watched, "Over.mkv", bytes: (1024 * 1024) + 1);

        await ScanAsync(library);

        Assert.Equal("unprocessed", await StatusAsync(library, "Within.mkv"));
        var over = await Scenario.FileRowAsync(library, "Over.mkv");
        Assert.Equal("skipped", (string)over!["status"]!);
        Assert.Contains("exceeds the 1 MB workflow maximum", (string)over["status_reason"]!, StringComparison.Ordinal);
        Assert.Null(over["skip_kind"]);
    }

    [Fact]
    public async Task A_file_that_cannot_be_opened_waits_unless_the_workflow_skips_the_readability_check()
    {
        var (checking, checkingWatched) = await WorkflowAsync("access-checked", ("skip_access_tests", false));
        var (trusting, trustingWatched) = await WorkflowAsync("access-skipped", ("skip_access_tests", true));
        using var first = new FileStream(Write(checkingWatched, "Film.mkv"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var second = new FileStream(Write(trustingWatched, "Film.mkv"), FileMode.Open, FileAccess.Read, FileShare.None);

        await ScanAsync(checking);
        await ScanAsync(trusting);

        Assert.Equal("on_hold", await StatusAsync(checking, "Film.mkv"));
        Assert.Equal("unprocessed", await StatusAsync(trusting, "Film.mkv"));
    }
}
