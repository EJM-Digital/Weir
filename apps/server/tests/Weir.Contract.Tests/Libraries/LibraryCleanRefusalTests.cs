using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// Everything that must leave a library file exactly as it was: a clean nobody confirmed, one asked for by someone who may not, a file
/// that is not Weir's to change, and a cleaned copy that does not check out. Each test ends by reading the file's bytes again.
/// </summary>
[ContractArea("libraries")]
public sealed class LibraryCleanRefusalTests
{
    private const string Film = "Film One (2020)/Film One (2020).mkv";

    private static readonly (string, JsonNode)[] WrittenByFfmpeg = [("remux_writer", "ffmpeg")];

    private static async Task<(LibraryCleanWorld World, string Path, string Hash)> ScannedFilmAsync(
        bool withViewer = false, params (string Name, JsonNode Value)[] workflow)
    {
        var world = await LibraryCleanWorld.StartAsync(withViewer: withViewer, workflow: [.. WrittenByFfmpeg, .. workflow]);
        try
        {
            var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
            var hash = LibraryCleanWorld.Sha256(path);
            await world.ScanAsync();
            return (world, path, hash);
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

    [RealFfmpegFact]
    public async Task A_clean_that_is_not_confirmed_is_refused_with_what_it_would_remove_and_changes_nothing()
    {
        var (world, path, hash) = await ScannedFilmAsync();
        await using var _ = world;

        var response = await world.CleanAsync([(string)(await world.FileAsync(path))["path"]!], confirmFinalRemoval: false);

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Equal("confirm_final_removal_required", (string)response.Fields["error"]!);
        Assert.Equal(1, (int)response.Fields["files_count"]!);
        Assert.Equal(2, (int)response.Fields["tracks_count"]!);
        Assert.True((long)response.Fields["estimated_bytes_saved"]! > 0);
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_viewer_cannot_clean_a_file()
    {
        var (world, path, hash) = await ScannedFilmAsync(withViewer: true);
        await using var _ = world;
        using var viewer = await SeededAccounts.SignInViewerAsync(world.Server);

        var response = await world.CleanAsync([(string)(await world.FileAsync(path))["path"]!], confirmFinalRemoval: true, viewer);

        response.ShouldBe(HttpStatusCode.Forbidden);
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_clean_from_nobody_signed_in_is_refused()
    {
        var (world, path, hash) = await ScannedFilmAsync();
        await using var _ = world;
        using var anonymous = world.Server.CreateClient();

        var response = await world.CleanAsync([(string)(await world.FileAsync(path))["path"]!], confirmFinalRemoval: true, anonymous);

        response.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_clean_without_a_valid_csrf_token_is_refused()
    {
        var (world, path, hash) = await ScannedFilmAsync();
        await using var _ = world;
        var url = $"{LibraryCleanWorld.LibrariesUrl}/{world.LibraryId}/library-files/clean";
        var body = LibraryCleanWorld.CleanBody([(string)(await world.FileAsync(path))["path"]!], confirmFinalRemoval: true);

        var missing = await world.Admin.PostAsync(url, body);
        body["csrf_token"] = "forged";
        var forged = await world.Admin.PostAsync(url, body);

        missing.ShouldBe(HttpStatusCode.UnprocessableEntity);
        forged.ShouldBe(HttpStatusCode.BadRequest);
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_file_with_nothing_to_remove_is_not_queued_and_is_left_untouched()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng"], []);
        var hash = LibraryCleanWorld.Sha256(path);
        await world.ScanAsync();
        Assert.Equal("matches", (string)(await world.FileAsync(path))["classification"]!);

        var response = await world.CleanAsync([(string)(await world.FileAsync(path))["path"]!], confirmFinalRemoval: true);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(0, (int)response.Fields["queued"]!);
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_file_that_matches_the_rules_by_the_time_the_clean_runs_is_left_untouched_and_says_so()
    {
        var (world, path, _) = await ScannedFilmAsync();
        await using var _ = world;
        // The scan saw two audio tracks. Before the clean runs, a media manager has put a film that already matches in their place.
        await world.PauseAsync(true);
        await world.ConfirmedCleanAsync(path);
        await RealMedia.GenerateAsync(path, seconds: 3, ["eng"], [], firstAudioDefault: true);
        var replacementHash = LibraryCleanWorld.Sha256(path);
        await world.PauseAsync(false);

        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("skipped", (string)clean["outcome"]!);
        Assert.Equal("This file already matches the workflow's rules; nothing to clean.", (string)clean["detail"]!);
        Assert.Equal(replacementHash, LibraryCleanWorld.Sha256(path));
        Assert.Equal([Film], world.FilesOnDisk());
    }

    [RealFfmpegFact]
    public async Task A_paused_Weir_cleans_nothing_and_cleans_once_when_processing_resumes()
    {
        var (world, path, hash) = await ScannedFilmAsync();
        await using var _ = world;
        await world.PauseAsync(true);

        var jobId = await world.ConfirmedCleanAsync(path);

        // What the server reports of the job is that no worker has had it. That a worker may not take it while paused is proved in
        // PauseGatesTests and AdmissionClaimTests, against the queue itself.
        var waiting = await world.JobAsync(jobId);
        Assert.Equal("pending", (string)waiting["status"]!);
        Assert.Null(waiting["lease_owner"]);
        Assert.Equal(LibraryCleanWorld.Sha256(path), hash);
        Assert.Single(world.FilesOnDisk());
        await world.PauseAsync(false);
        var clean = await world.WaitForCleanAsync(path);
        Assert.Equal("cleaned", (string)clean["outcome"]!);
        Assert.Equal(["audio", "video"], (await RealMedia.TracksAsync(path)).Select(track => track.Kind).Order());
        Assert.Single(await world.CleanJobsAsync());
    }

    [RealFfmpegFact]
    public async Task A_file_set_aside_with_leave_alone_is_not_cleaned()
    {
        var (world, path, hash) = await ScannedFilmAsync();
        await using var _ = world;
        var scanned = (string)(await world.FileAsync(path))["path"]!;
        var aside = await world.Admin.PostWithCsrfAsync(
            $"{LibraryCleanWorld.LibrariesUrl}/{world.LibraryId}/library-files/leave-alone", Obj(("path", scanned), ("leave_alone", true)));
        aside.ShouldBe(HttpStatusCode.OK);

        var response = await world.CleanAsync([scanned], confirmFinalRemoval: true);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(0, (int)response.Fields["queued"]!);
        Assert.Equal([scanned], StringList(response.Fields["skipped_paths"]));
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_file_still_shared_with_a_download_is_never_queued()
    {
        var (world, path, hash) = await ScannedFilmAsync();
        await using var _ = world;
        var seeding = Path.Combine(world.DownloadsFolder, "Film.One.2020.mkv");
        await LibraryCleanWorld.HardLinkAsync(path, seeding);
        var scanned = (string)(await world.FileAsync(path))["path"]!;

        var response = await world.CleanAsync([scanned], confirmFinalRemoval: true);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(0, (int)response.Fields["queued"]!);
        Assert.Equal([scanned], StringList(response.Fields["skipped_paths"]));
        Assert.NotEmpty(response.Fields["warnings"]!.AsArray());
        Assert.Equal(hash, LibraryCleanWorld.Sha256(seeding));
        await AssertUntouchedAsync(world, path, hash);
    }

    [RealFfmpegFact]
    public async Task A_cleaned_copy_that_does_not_check_out_is_never_put_in_place()
    {
        await using var world = await LibraryCleanWorld.StartAsync(workflow: WrittenByFfmpeg);
        var path = await world.AddFilmAsync(Film, ["eng", "fre"], ["eng"]);
        // Cut well short of the end: the container header still names the whole duration, so only reading the copy to its end shows it is short.
        var whole = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, whole[..(whole.Length * 60 / 100)]);
        var hash = LibraryCleanWorld.Sha256(path);
        await world.ScanAsync();

        await world.ConfirmedCleanAsync(path);
        var clean = await world.WaitForCleanAsync(path);

        Assert.Equal("failed", (string)clean["outcome"]!);
        Assert.Equal(hash, LibraryCleanWorld.Sha256(path));
        Assert.Equal([Film], world.FilesOnDisk());
    }
}
