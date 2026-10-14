using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// "Keep the original after clean": the film Weir replaced is moved aside whole instead of deleted, and nothing is ever overwritten to do
/// it. With the setting off, the original is gone once the cleaned file is in place and no second copy is left behind.
/// </summary>
[ContractArea("libraries")]
public sealed class LibraryCleanKeepsOriginalTests
{
    private const string Film = "Film One (2020)/Film One (2020).mkv";
    private const string KeptFilm = ".weir-originals/Film One (2020)/Film One (2020).mkv";

    private static readonly (string, System.Text.Json.Nodes.JsonNode)[] WrittenByFfmpeg = [("remux_writer", "ffmpeg")];

    [RealFfmpegFact]
    public async Task With_keep_original_on_the_original_is_kept_whole_beside_the_cleaned_file()
    {
        await using var world = await LibraryCleanWorld.StartAsync(keepOriginal: true, workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        var originalHash = LibraryCleanWorld.Sha256(path);
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        var clean = await world.WaitForCleanAsync(path);

        var kept = Path.Combine(world.Folder, KeptFilm);
        Assert.Equal("cleaned", (string)clean["outcome"]!);
        var keptNote = System.Text.RegularExpressions.Regex.Match((string)clean["detail"]!, @"The original was kept at (?<path>.+).$");
        Assert.True(keptNote.Success, (string)clean["detail"]!);
        Assert.True(LibraryCleanWorld.SamePath(keptNote.Groups["path"].Value, kept), keptNote.Groups["path"].Value);
        Assert.Equal(originalHash, LibraryCleanWorld.Sha256(kept));
        Assert.Equal(
            [new RealMedia.Track("video", string.Empty, "mpeg4"), new RealMedia.Track("audio", "eng", "mp2"), new RealMedia.Track("audio", "fre", "mp2"), new RealMedia.Track("subtitle", "eng", "subrip")],
            await RealMedia.TracksAsync(kept));
        Assert.Equal([new RealMedia.Track("video", string.Empty, "mpeg4"), new RealMedia.Track("audio", "eng", "mp2")], await RealMedia.TracksAsync(path));
        Assert.Equal([KeptFilm, Film], world.FilesOnDisk());
        world.AssertNoSwapLeftovers();
    }

    [RealFfmpegFact]
    public async Task A_kept_original_is_not_listed_as_a_library_file_at_the_next_scan()
    {
        await using var world = await LibraryCleanWorld.StartAsync(keepOriginal: true, workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        await world.ScanAsync();
        await world.ConfirmedCleanAsync(path);
        await world.WaitForCleanAsync(path);

        await world.ScanAsync();

        var files = (await world.FilesBodyAsync())["files"]!.AsArray();
        Assert.Single(files);
        Assert.True(LibraryCleanWorld.SamePath((string)files[0]!["path"]!, path));
    }

    [RealFfmpegFact]
    public async Task With_keep_original_off_no_second_copy_is_left()
    {
        await using var world = await LibraryCleanWorld.StartAsync(keepOriginal: false, workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.DoesNotContain("kept", (string)clean["detail"]!, StringComparison.Ordinal);
        Assert.Equal([Film], world.FilesOnDisk());
        Assert.False(Directory.Exists(world.OriginalsFolder));
    }

    [RealFfmpegFact]
    public async Task A_kept_original_never_overwrites_a_file_already_in_the_originals_folder()
    {
        await using var world = await LibraryCleanWorld.StartAsync(keepOriginal: true, workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        var originalHash = LibraryCleanWorld.Sha256(path);
        var taken = Path.Combine(world.Folder, KeptFilm);
        Directory.CreateDirectory(Path.GetDirectoryName(taken)!);
        await File.WriteAllTextAsync(taken, "an earlier original");
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        await world.WaitForCleanAsync(path);

        var numbered = Path.Combine(world.Folder, ".weir-originals/Film One (2020)/Film One (2020) (2).mkv");
        Assert.Equal("an earlier original", await File.ReadAllTextAsync(taken));
        Assert.Equal(originalHash, LibraryCleanWorld.Sha256(numbered));
        world.AssertNoSwapLeftovers();
    }

    [RealFfmpegFact]
    public async Task A_workflow_with_its_own_originals_folder_keeps_the_original_there()
    {
        await using var world = await LibraryCleanWorld.StartAsync(keepOriginal: true, customOriginalsFolder: true, workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        var originalHash = LibraryCleanWorld.Sha256(path);
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        await world.WaitForCleanAsync(path);

        Assert.Equal(originalHash, LibraryCleanWorld.Sha256(Path.Combine(world.CustomOriginalsFolder, "Film One (2020)/Film One (2020).mkv")));
        Assert.Equal([Film], world.FilesOnDisk());
    }
}
