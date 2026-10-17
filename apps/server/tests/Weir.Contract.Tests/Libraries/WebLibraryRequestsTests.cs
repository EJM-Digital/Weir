using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// The requests the Library page sends that remove tracks for good, switch on a clean nobody watches, change the library's setup or ask a
/// media manager to delete a file, sent exactly as the web app builds them (<c>fixtures/web-bodies</c>, written by the web tests) to a real
/// server working real media. Each test that sends one without the person's confirmation ends by reading the file's bytes again.
/// </summary>
[ContractArea("libraries")]
public sealed class WebLibraryRequestsTests
{
    private const string Film = "Film One (2020)/Film One (2020).mkv";

    private static readonly (string, JsonNode)[] WrittenByFfmpeg = [("remux_writer", "ffmpeg")];

    /// <summary>A scanned film with French audio before English audio, so the rules (English only) and a person's own choice (track 1, the French) differ.</summary>
    private static async Task<(LibraryCleanWorld World, string Path, WebSample Sample)> ScannedFilmAsync()
    {
        var world = await LibraryCleanWorld.StartAsync(workflow: WrittenByFfmpeg);
        try
        {
            var path = await world.AddFilmAsync(Film, ["fre", "eng"], ["eng"]);
            await world.ScanAsync();
            var sample = new WebSample(
                LibraryId: world.LibraryId,
                Path: (string)(await world.FileAsync(path))["path"]!,
                LibraryFolder: world.Folder,
                SizeBytes: new FileInfo(path).Length);
            return (world, path, sample);
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertUntouchedAsync(LibraryCleanWorld world, string path, string hash)
    {
        Assert.Equal(hash, LibraryCleanWorld.Sha256(path));
        Assert.Equal([Film], world.FilesOnDisk());
        Assert.Empty(await world.CleanJobsAsync());
    }

    private static async Task<bool> ScheduleOnAsync(LibraryCleanWorld world)
    {
        var settings = await world.Admin.GetAsync($"{LibraryCleanWorld.LibrariesUrl}/{world.LibraryId}/library-settings");
        settings.ShouldBe(HttpStatusCode.OK);
        return (bool)settings.Fields["library_schedule_enabled"]!;
    }

    [RealFfmpegFact]
    public async Task A_clean_the_person_has_not_confirmed_is_asked_to_confirm_and_changes_nothing()
    {
        var (world, path, sample) = await ScannedFilmAsync();
        await using var _ = world;
        var hash = LibraryCleanWorld.Sha256(path);

        var response = await WebBodies.Get("clean/to-confirm", sample).SendAsync(world.Admin);

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Equal("confirm_final_removal_required", (string)response.Fields["error"]!);
        Assert.Equal(1, (int)response.Fields["files_count"]!);
        Assert.Equal(2, (int)response.Fields["tracks_count"]!);
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_confirmed_clean_removes_the_tracks_the_rules_do_not_keep()
    {
        var (world, path, sample) = await ScannedFilmAsync();
        await using var _ = world;

        var response = await WebBodies.Get("clean/confirmed", sample).SendAsync(world.Admin);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(1, (int)response.Fields["queued"]!);
        var clean = await world.WaitForCleanAsync(path);
        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.Equal(["eng"], (await RealMedia.TracksAsync(path)).Where(track => track.Kind == "audio").Select(track => track.Language));
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task A_choice_of_tracks_the_person_has_not_confirmed_is_asked_to_confirm_and_changes_nothing()
    {
        var (world, path, sample) = await ScannedFilmAsync();
        await using var _ = world;
        var hash = LibraryCleanWorld.Sha256(path);

        var response = await WebBodies.Get("clean/chosen-tracks-to-confirm", sample).SendAsync(world.Admin);

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Equal("confirm_final_removal_required", (string)response.Fields["error"]!);
        Assert.Equal(1, (int)response.Fields["files_count"]!);
        Assert.Equal(1, (int)response.Fields["tracks_count"]!);
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_confirmed_choice_of_tracks_keeps_the_tracks_the_person_chose_and_not_the_ones_the_rules_would()
    {
        var (world, path, sample) = await ScannedFilmAsync();
        await using var _ = world;

        var response = await WebBodies.Get("clean/chosen-tracks-confirmed", sample).SendAsync(world.Admin);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(1, (int)response.Fields["queued"]!);
        var clean = await world.WaitForCleanAsync(path);
        Assert.Equal("cleaned", (string)clean["outcome"]!);
        var tracks = await RealMedia.TracksAsync(path);
        Assert.Equal(["fre"], tracks.Where(track => track.Kind == "audio").Select(track => track.Language));
        Assert.Equal(["eng"], tracks.Where(track => track.Kind == "subtitle").Select(track => track.Language));
    }

    [RealFfmpegFact]
    public async Task A_choice_of_tracks_made_for_another_size_of_file_is_not_applied()
    {
        var (world, path, sample) = await ScannedFilmAsync();
        await using var _ = world;
        var hash = LibraryCleanWorld.Sha256(path);

        var response = await WebBodies.Get("clean/chosen-tracks-confirmed", sample with { SizeBytes = sample.SizeBytes + 1 }).SendAsync(world.Admin);

        response.ShouldBe(HttpStatusCode.OK);
        var clean = await world.WaitForCleanAsync(path);
        Assert.Equal("failed", (string)clean["outcome"]!);
        Assert.Equal("This file changed after its tracks were chosen, so Weir left it alone. Open it again and choose once more.", (string)clean["detail"]!);
        Assert.Equal(hash, LibraryCleanWorld.Sha256(path));
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task Switching_the_daily_clean_on_asks_to_confirm_while_a_file_would_lose_tracks_and_leaves_it_off()
    {
        var (world, path, sample) = await ScannedFilmAsync();
        await using var _ = world;
        var hash = LibraryCleanWorld.Sha256(path);

        var response = await WebBodies.Get("schedule/on-to-confirm", sample).SendAsync(world.Admin);

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Equal("confirm_final_removal_required", (string)response.Fields["error"]!);
        Assert.False(await ScheduleOnAsync(world));
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task Switching_the_daily_clean_on_once_confirmed_turns_it_on_and_switching_it_off_needs_no_confirmation()
    {
        var (world, _, sample) = await ScannedFilmAsync();
        await using var _ = world;

        var on = await WebBodies.Get("schedule/on-confirmed", sample).SendAsync(world.Admin);
        var isOn = await ScheduleOnAsync(world);
        var off = await WebBodies.Get("schedule/off", sample).SendAsync(world.Admin);

        on.ShouldBe(HttpStatusCode.OK);
        Assert.True((bool)on.Fields["library_schedule_enabled"]!);
        Assert.True(isOn);
        off.ShouldBe(HttpStatusCode.OK);
        Assert.False((bool)off.Fields["library_schedule_enabled"]!);
        Assert.False(await ScheduleOnAsync(world));
    }

    [RealFfmpegFact]
    public async Task Saving_the_library_setup_stores_what_the_web_app_sends()
    {
        var (world, _, sample) = await ScannedFilmAsync();
        await using var _ = world;

        var saved = await WebBodies.Get("library-settings/save", sample).SendAsync(world.Admin);

        saved.ShouldBe(HttpStatusCode.OK);
        var read = await world.Admin.GetAsync($"{LibraryCleanWorld.LibrariesUrl}/{world.LibraryId}/library-settings");
        read.ShouldBe(HttpStatusCode.OK);
        Assert.Equal([world.Folder], StringList(read.Fields["library_folders"]));
        Assert.False((bool)read.Fields["clean_hardlinked_files"]!);
        Assert.True((bool)read.Fields["skip_if_manager_would_redownload"]!);
        Assert.True((bool)read.Fields["keep_original_after_clean"]!);
        Assert.Equal(string.Empty, (string)read.Fields["originals_folder"]!);
    }

    [RealFfmpegFact]
    public async Task Download_again_without_the_confirmation_is_refused_and_nothing_is_asked_of_anyone()
    {
        var (world, path, sample) = await ScannedFilmAsync();
        await using var _ = world;
        var hash = LibraryCleanWorld.Sha256(path);

        var response = await WebBodies.Get("download-again/confirmed", sample).With("confirm_destructive", false).SendAsync(world.Admin);

        response.ShouldBe(HttpStatusCode.BadRequest);
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task Download_again_for_a_file_no_manager_supplied_says_so_and_leaves_the_file()
    {
        var (world, path, sample) = await ScannedFilmAsync();
        await using var _ = world;
        var hash = LibraryCleanWorld.Sha256(path);

        var response = await WebBodies.Get("download-again/confirmed", sample).SendAsync(world.Admin);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal("unsupported", (string)response.Fields["outcome"]!);
        await AssertUntouchedAsync(world, path, hash);
    }
}
