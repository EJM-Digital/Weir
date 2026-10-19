using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// "Keep the original's date" on a workflow: the file Weir writes to the output folder carries the modified time of the file it was made
/// from, or the time it was written when the workflow does not keep it. Run with the real ffmpeg on a real file.
/// </summary>
[ContractArea("processing")]
public sealed class PreserveOriginalTimestampsTests
{
    private static readonly DateTime LongAgo = new(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    [RealFfmpegTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cleaned_file_carries_the_date_of_the_original_only_when_the_workflow_keeps_it(bool preserve)
    {
        await using var scenario = await Scenario.StartWithRealToolsAsync();
        await scenario.DelunoSetupAsync(library: [("preserve_original_timestamps", preserve)]);
        var source = Path.Combine(scenario.Folders.Watched, "Dated.Film.2019", "dated.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RealMedia.GenerateAsync(source, seconds: 2, "eng", "fre");
        File.SetLastWriteTimeUtc(source, LongAgo);

        await scenario.PostHandoffAsync("handoff-dated", source);
        await scenario.WaitForHandoffStateAsync("handoff-dated", "completed");

        var written = File.GetLastWriteTimeUtc(Path.Combine(scenario.Folders.Output, "Dated.Film.2019", "dated.mkv"));
        if (preserve)
        {
            Assert.Equal(LongAgo, written);
        }
        else
        {
            Assert.True(written > LongAgo.AddYears(1), $"the file Weir wrote is dated {written:O}, not the time it was written");
        }
    }
}
