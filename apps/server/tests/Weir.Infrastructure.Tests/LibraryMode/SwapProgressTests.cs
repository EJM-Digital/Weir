using Weir.Core.Configuration;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.LibraryMode;

namespace Weir.Infrastructure.Tests.LibraryMode;

public sealed class SwapProgressTests : IDisposable
{
    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public async Task A_swap_reports_every_point_in_order_as_it_commits()
    {
        var scenario = new SwapScenario();
        var progress = new RecordingProgress();
        var swap = new SafeSwap(scenario.Files, scenario.Journal, scenario.Validator, scenario.SwapLogger, progress: progress);

        var result = await swap.RunAsync(SwapScenario.JobId, SwapScenario.Original, WriteNew(scenario));

        Assert.Equal(SwapOutcome.Committed, result.Outcome);
        Assert.Equal(Enum.GetValues<SwapPoint>(), progress.Points);
    }

    [Fact]
    public async Task Each_point_is_reported_after_the_work_it_names_has_happened()
    {
        var scenario = new SwapScenario();
        var seen = new Dictionary<SwapPoint, (bool Original, bool Backup, bool Temp, SwapJournalState? Journal)>();
        var progress = new RecordingProgress(point =>
            seen[point] = (
                scenario.Files.Has(SwapScenario.Original),
                scenario.Files.Has(SwapScenario.Backup),
                scenario.Files.Has(SwapScenario.Temp),
                scenario.Journal.Latest(SwapScenario.JobId)?.State));
        var swap = new SafeSwap(scenario.Files, scenario.Journal, scenario.Validator, scenario.SwapLogger, progress: progress);

        await swap.RunAsync(SwapScenario.JobId, SwapScenario.Original, WriteNew(scenario));

        Assert.Equal((true, false, false, SwapJournalState.Writing), seen[SwapPoint.Started]);
        Assert.Equal((true, false, true, SwapJournalState.Writing), seen[SwapPoint.CopyChecked]);
        Assert.Equal((true, false, true, SwapJournalState.Committing), seen[SwapPoint.CommitRecorded]);
        Assert.Equal((false, true, true, SwapJournalState.Committing), seen[SwapPoint.OriginalMovedAside]);
        Assert.Equal((true, true, false, SwapJournalState.Committing), seen[SwapPoint.CleanedCopyInPlace]);
        Assert.Equal((true, true, false, SwapJournalState.Committed), seen[SwapPoint.CommittedRecorded]);
        Assert.Equal((true, false, false, SwapJournalState.Committed), seen[SwapPoint.BackupDealtWith]);
        Assert.Equal((true, false, false, SwapJournalState.Finished), seen[SwapPoint.FinishedRecorded]);
    }

    [Theory]
    [InlineData("OriginalMovedAside", SwapPoint.OriginalMovedAside)]
    [InlineData("originalmovedaside", SwapPoint.OriginalMovedAside)]
    [InlineData("Started", SwapPoint.Started)]
    public void A_setting_names_the_point_to_hold_at(string setting, SwapPoint expected)
    {
        var progress = HeldSwapProgress.FromSetting(setting, _home.Path);

        // Any other point passes straight through; the held one is covered below.
        foreach (var point in Enum.GetValues<SwapPoint>().Where(point => point != expected))
        {
            progress.Reached(point);
        }

        Assert.False(File.Exists(Path.Join(_home.Path, HeldSwapProgress.HeldFileName)));
    }

    [Theory]
    [InlineData("not_a_point")]
    [InlineData("99")]
    public void A_setting_that_names_no_point_is_refused_with_the_names_that_work(string setting)
    {
        var refused = Assert.Throws<WeirConfigurationException>(() => HeldSwapProgress.FromSetting(setting, _home.Path));

        Assert.StartsWith($"WEIR_LIBRARY_SWAP_HOLD_AT is '{setting}', which is not one of: Started, CopyChecked, CommitRecorded, ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reaching_the_held_point_says_so_in_the_home_folder_and_stays_there()
    {
        var progress = HeldSwapProgress.FromSetting(nameof(SwapPoint.CleanedCopyInPlace), _home.Path);
        var held = Path.Join(_home.Path, HeldSwapProgress.HeldFileName);

        // On a thread of its own: it never comes back, which is the point.
        var reached = Task.Factory.StartNew(() => progress.Reached(SwapPoint.CleanedCopyInPlace), TaskCreationOptions.LongRunning);
        await Eventually.ThatAsync(() => File.Exists(held));

        Assert.Equal("CleanedCopyInPlace", await ReadWhileRenamedAsync(held));
        Assert.False(reached.IsCompleted);
        Assert.False(File.Exists(held + ".tmp"));
    }

    /// <summary>
    /// The file's text, read with a share mode that lets the writer's rename finish. The file appears under its name at the moment of
    /// the rename, and Windows keeps the renaming handle (it holds delete access) open for an instant after: a read that does not share
    /// delete (`File.ReadAllText` does not) is refused as "used by another process" if it lands in that instant.
    /// </summary>
    private static async Task<string> ReadWhileRenamedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static SwapOutputWriter WriteNew(SwapScenario scenario) =>
        (temp, _) =>
        {
            scenario.Files.Write(temp, "NEW");
            return Task.CompletedTask;
        };

    private sealed class RecordingProgress(Action<SwapPoint>? onReached = null) : ISwapProgress
    {
        public List<SwapPoint> Points { get; } = [];

        public void Reached(SwapPoint point)
        {
            Points.Add(point);
            onReached?.Invoke(point);
        }
    }
}
