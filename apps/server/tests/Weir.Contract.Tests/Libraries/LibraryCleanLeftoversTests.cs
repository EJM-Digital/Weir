using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// A swap that was interrupted, or a file another program holds, must never cost the film. Whatever step stopped, exactly one intact
/// file is left under the original name, and the next clean or the next start puts the rest right.
/// </summary>
[ContractArea("libraries")]
public sealed class LibraryCleanLeftoversTests
{
    private const string Film = "Film One (2020)/Film One (2020).mkv";
    private const string Backup = "Film One (2020)/Film One (2020).weir-bak.mkv";
    private const string Temp = "Film One (2020)/Film One (2020).weir-tmp.mkv";

    private static readonly (string, System.Text.Json.Nodes.JsonNode)[] WrittenByFfmpeg = [("remux_writer", "ffmpeg")];

    [RealFfmpegFact]
    public async Task A_copy_left_by_an_interrupted_swap_is_cleared_before_the_next_clean()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        await File.WriteAllTextAsync(Path.Combine(world.Folder, Temp), "half a copy");
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.Equal([new RealMedia.Track("video", string.Empty, "mpeg4"), new RealMedia.Track("audio", "eng", "mp2")], await RealMedia.TracksAsync(path));
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task A_backup_left_without_its_film_is_put_back_when_Weir_starts()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        var hash = LibraryCleanWorld.Sha256(path);
        // A crash after the original was moved aside and before the cleaned copy took its place.
        File.Move(path, Path.Combine(world.Folder, Backup));
        await File.WriteAllTextAsync(Path.Combine(world.Folder, Temp), "half a copy");

        await world.RestartServerAsync();

        await Poll.UntilAsync(() => Task.FromResult(File.Exists(path)), "the film to be put back from its backup");
        Assert.Equal(hash, LibraryCleanWorld.Sha256(path));
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task A_backup_left_beside_a_film_that_is_in_place_is_removed_when_Weir_starts()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng"], []);
        var hash = LibraryCleanWorld.Sha256(path);
        // A crash after the cleaned copy was put in place and before the backup was deleted.
        await File.WriteAllTextAsync(Path.Combine(world.Folder, Backup), "the replaced original");

        await world.RestartServerAsync();

        await Poll.UntilAsync(() => Task.FromResult(!File.Exists(Path.Combine(world.Folder, Backup))), "the backup to be removed");
        Assert.Equal(hash, LibraryCleanWorld.Sha256(path));
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegWindowsFact]
    public async Task A_file_another_program_holds_is_waited_for_and_cleaned_once_it_is_released()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        var hash = LibraryCleanWorld.Sha256(path);
        await world.ScanAsync();

        long jobId;
        // Open for reading but not for renaming or deleting, as a player or a scanner holds a film.
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            jobId = await world.ConfirmedCleanAsync(path);
            var postponed = await Poll.UntilAsync(
                async () =>
                {
                    var job = await world.JobAsync(jobId);
                    return (string)job["status"]! == "pending" && ((string)job["payload_json"]!).Contains("in_use_attempts", StringComparison.Ordinal) ? job : null;
                },
                "the clean to be put off because the file is in use");
            Assert.Contains("\"in_use_attempts\":1", (string)postponed["payload_json"]!, StringComparison.Ordinal);
            Assert.Equal(hash, LibraryCleanWorld.Sha256(path));
            Assert.Equal([Film], world.FilesOnDisk());
        }

        await world.MakeJobDueAsync(jobId);
        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.Equal([new RealMedia.Track("video", string.Empty, "mpeg4"), new RealMedia.Track("audio", "eng", "mp2")], await RealMedia.TracksAsync(path));
        Assert.Equal([Film], world.FilesOnDisk());
    }
}
