using System.Net;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.Processing.RemuxPass;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// While processing is paused nothing changes a media file, and that includes the two things a person can ask for by hand that
/// reach a file: deleting a failed download from Activity's remove dialog, and "Download again" on a library file. Each is refused
/// with the reason, nothing on disk and no manager is touched, and the same request goes through once processing is resumed.
/// </summary>
public sealed class PausedActionsApiTests
{
    private const string PausedMessageStart = "Processing is paused";

    private static async Task PauseAsync(ApiTestClient client, bool paused)
    {
        using var response = await client.PutAsync("/api/v1/pause", new { csrf_token = await client.CsrfAsync(), paused, scan_while_paused = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_failed_download_by_hand_is_refused_while_paused_and_goes_through_once_resumed()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var watched = Path.Combine(server.Home, "watch");
        Directory.CreateDirectory(watched);
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Hand deleted movies', 'movie', $w, $o, $k, 1) RETURNING id",
            ("$w", watched), ("$o", Path.Combine(server.Home, "out")), ("$k", Path.Combine(server.Home, "work")));
        var path = Path.Combine(watched, "film.mkv");
        await File.WriteAllBytesAsync(path, "a failed download"u8.ToArray());
        var fingerprint = SourceFiles.Fingerprint(path);
        var fileId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, last_seen_at, fingerprint_size_bytes, fingerprint_mtime_ns) " +
            "VALUES ($lib, 'film.mkv', 'processing_failed', 'Weir could not read the audio track.', $size, CURRENT_TIMESTAMP, $fsize, $fmtime) RETURNING id",
            ("$lib", libraryId), ("$size", fingerprint.SizeBytes), ("$fsize", fingerprint.SizeBytes), ("$fmtime", fingerprint.ModifiedTimeNs));
        await PauseAsync(client, paused: true);

        using var refused = await client.SendAsync(
            HttpMethod.Delete, $"/api/v1/processing/files/{fileId}", new { csrf_token = await client.CsrfAsync(), resolution = "delete" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.StartsWith(PausedMessageStart, await Detail(refused), StringComparison.Ordinal);
        Assert.True(File.Exists(path), "a paused Weir must not delete the download");
        Assert.Equal("a failed download", await File.ReadAllTextAsync(path));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM files"));

        await PauseAsync(client, paused: false);
        using var done = await client.SendAsync(
            HttpMethod.Delete, $"/api/v1/processing/files/{fileId}", new { csrf_token = await client.CsrfAsync(), resolution = "delete" });

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.False(File.Exists(path));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task Download_again_is_refused_while_paused_without_asking_the_manager_and_goes_through_once_resumed()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        rig.Manager.RadarrThatDeletesAndSearches();
        await PauseAsync(rig.Admin, paused: true);

        using var refused = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.StartsWith(PausedMessageStart, await Detail(refused), StringComparison.Ordinal);
        Assert.Empty(rig.Manager.Asked());

        await PauseAsync(rig.Admin, paused: false);
        using var done = await rig.RedownloadAsync(rig.Admin, libraryId, RedownloadRig.FilmPath);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.NotEmpty(rig.Manager.Asked());
    }
}
