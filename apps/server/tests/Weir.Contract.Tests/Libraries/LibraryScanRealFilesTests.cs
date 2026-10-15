using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// A library scan reads what is in the folder with the real ffprobe and lists it, so the Files table shows each film as the file
/// is: the tracks it holds, and whether the workflow's rules would change it. The folder holds real media generated for the test.
/// </summary>
[ContractArea("libraries")]
public sealed class LibraryScanRealFilesTests
{
    [RealFfmpegFact]
    public async Task A_scan_lists_each_real_file_with_the_tracks_ffprobe_finds_and_what_the_rules_would_do()
    {
        await using var world = await LibraryCleanWorld.StartAsync();
        var untidy = await world.AddFilmAsync("Film One (2020)/Film One (2020).mkv", ["eng", "fre"], ["eng"]);
        var tidy = await world.AddFilmAsync("Film Two (2021)/Film Two (2021).mkv", ["eng"], []);

        await world.ScanAsync();

        var files = (await world.FilesBodyAsync())["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        var untidyRow = await world.FileAsync(untidy);
        Assert.Equal("would_change", (string)untidyRow["classification"]!);
        Assert.Equal(2, (int)untidyRow["audio_track_count"]!);
        Assert.Equal(1, (int)untidyRow["subtitle_track_count"]!);
        Assert.Equal(1, (int)untidyRow["removed_audio_tracks"]!);
        Assert.Equal(1, (int)untidyRow["removed_subtitle_tracks"]!);
        var tidyRow = await world.FileAsync(tidy);
        Assert.Equal("matches", (string)tidyRow["classification"]!);
        Assert.Equal(1, (int)tidyRow["audio_track_count"]!);
        Assert.Equal(0, (int)tidyRow["subtitle_track_count"]!);
    }
}
