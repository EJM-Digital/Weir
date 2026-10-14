using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// Cleaning a library file replaces it where it stands, and that is the one path where a fault loses a film with no second copy. Each
/// test scans a library of real media, queues the clean the web sends once the person has confirmed, and reads the result back with
/// the real ffprobe: the file at the same path holds only the wanted tracks, plays to its end, and nothing of the swap is left beside it.
/// </summary>
[ContractArea("libraries")]
public sealed class LibraryCleanReplacesFileTests
{
    private const string Film = "Film One (2020)/Film One (2020).mkv";

    [RealMkvmergeFact]
    public async Task A_cleaned_mkv_is_written_by_mkvmerge_in_place_with_only_the_wanted_tracks()
    {
        await using var world = await LibraryCleanWorld.StartAsync();
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        var sizeBefore = new FileInfo(path).Length;
        await world.ScanAsync();

        var row = await world.FileAsync(path);
        Assert.Equal("would_change", (string)row["classification"]!);
        Assert.Equal("needs_cleaning", (string)row["status"]!);
        Assert.Equal(1, (int)row["removed_audio_tracks"]!);
        Assert.Equal(1, (int)row["removed_subtitle_tracks"]!);

        await world.ConfirmedCleanAsync(path);
        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.Equal([new RealMedia.Track("video", string.Empty, "mpeg4"), new RealMedia.Track("audio", "eng", "mp2")], await RealMedia.TracksAsync(path));
        Assert.Contains("mkvmerge", await RealMedia.MkvmergeVersionAsync(path), StringComparison.Ordinal);
        await RealMedia.AssertPlaysThroughAsync(path);
        Assert.True(new FileInfo(path).Length < sizeBefore, "the cleaned file should be smaller than the one it replaced");
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task A_cleaned_mkv_is_written_by_ffmpeg_when_the_workflow_asks_for_it()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: [("remux_writer", "ffmpeg")]);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.Equal([new RealMedia.Track("video", string.Empty, "mpeg4"), new RealMedia.Track("audio", "eng", "mp2")], await RealMedia.TracksAsync(path));
        Assert.DoesNotContain("mkvmerge", await RealMedia.MkvmergeVersionAsync(path), StringComparison.Ordinal);
        await RealMedia.AssertPlaysThroughAsync(path);
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task A_cleaned_mp4_is_replaced_in_place_with_only_the_wanted_tracks()
    {
        await using var world = await LibraryCleanWorld.StartAsync();
        var path = await world.AddFilmAsync("Film Two (2021)/Film Two (2021).mp4", ["eng", "fre"], ["eng"]);
        var sizeBefore = new FileInfo(path).Length;
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.Equal([new RealMedia.Track("video", string.Empty, "mpeg4"), new RealMedia.Track("audio", "eng", "aac")], await RealMedia.TracksAsync(path));
        await RealMedia.AssertPlaysThroughAsync(path);
        Assert.NotEqual(sizeBefore, new FileInfo(path).Length);
        Assert.Equal(["Film Two (2021)/Film Two (2021).mp4"], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task A_file_shared_with_a_download_is_cleaned_when_the_workflow_allows_it_and_the_download_keeps_its_own_copy()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: [("remux_writer", "ffmpeg")]);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        var seeding = Path.Combine(world.DownloadsFolder, "Film.One.2020.mkv");
        await LibraryCleanWorld.HardLinkAsync(path, seeding);
        var hash = LibraryCleanWorld.Sha256(seeding);
        await world.AllowHardlinkedFilesAsync();
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.Equal([new RealMedia.Track("video", string.Empty, "mpeg4"), new RealMedia.Track("audio", "eng", "mp2")], await RealMedia.TracksAsync(path));
        Assert.Equal(hash, LibraryCleanWorld.Sha256(seeding));
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task A_file_Weir_has_cleaned_reads_as_matching_the_rules_at_the_next_scan()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: [("remux_writer", "ffmpeg")]);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        await world.ScanAsync();
        await world.ConfirmedCleanAsync(path);
        await world.WaitForCleanAsync(path);

        await world.ScanAsync();

        var row = await world.FileAsync(path);
        Assert.Equal("matches", (string)row["classification"]!);
        Assert.Equal("matches", (string)row["status"]!);
        Assert.Equal(1, (int)row["audio_track_count"]!);
        Assert.Equal(0, (int)row["subtitle_track_count"]!);
    }

    [RealFfmpegFact]
    public async Task Cleaning_a_file_gives_it_a_new_modification_time_even_when_the_workflow_preserves_original_timestamps()
    {
        // The setting is for the files Weir writes to an output folder. A cleaned library file is a different file to the media
        // manager that watches it, and it notices that by the size and modification time changing.
        await using var world = await LibraryCleanWorld.StartAsync(workflow: [("remux_writer", "ffmpeg"), ("preserve_original_timestamps", true)]);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        var longAgo = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, longAgo);
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        await world.WaitForCleanAsync(path);

        Assert.True(File.GetLastWriteTimeUtc(path) > longAgo.AddYears(1), "the cleaned file should carry the time it was written");
    }
}
