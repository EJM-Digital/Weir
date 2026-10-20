using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weir.Api.Tests.Platform;
using Weir.Tests.Shared;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// skip_if_manager_would_redownload over real HTTP: it is on for a library nobody has set up, a save changes only what it names, and what the
/// Library page's Clean does follows it. A file whose removed audio would put its title below the cutoff of its Radarr profile is left out of the
/// clean while the setting is on, and queued once it is off. The manager's replies are those of <see cref="RedownloadRiskReplies"/>.
/// </summary>
public sealed class LibrarySkipRedownloadApiTests
{
    private const string EnglishAndJapanese =
        """{"format":{"duration":"100.0"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"},"disposition":{"default":1}},{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"}}]}""";

    private static string SettingsPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-settings";

    private static string CleanPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-files/clean";

    [Fact]
    public async Task A_library_nobody_has_set_up_skips_a_clean_the_manager_would_undo()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var libraryId = await rig.AddUnlinkedFilmAsync();

        using var response = await rig.Admin.GetAsync(SettingsPath(libraryId));

        Assert.True((await Json(response))["skip_if_manager_would_redownload"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_save_changes_the_setting_only_when_it_names_it()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var libraryId = await rig.AddUnlinkedFilmAsync();

        var off = await SaveAsync(rig.Admin, libraryId, new { skip_if_manager_would_redownload = false });
        var left = await SaveAsync(rig.Admin, libraryId, new { clean_hardlinked_files = true });
        var on = await SaveAsync(rig.Admin, libraryId, new { skip_if_manager_would_redownload = true });

        Assert.Equal(
            (false, false, true),
            (off["skip_if_manager_would_redownload"]!.GetValue<bool>(), left["skip_if_manager_would_redownload"]!.GetValue<bool>(), on["skip_if_manager_would_redownload"]!.GetValue<bool>()));
        using var read = await rig.Admin.GetAsync(SettingsPath(libraryId));
        Assert.True((await Json(read))["skip_if_manager_would_redownload"]!.GetValue<bool>());
    }

    [Fact]
    public async Task With_the_setting_on_a_clean_leaves_the_file_out_and_with_it_off_the_file_is_queued()
    {
        await using var rig = await RedownloadRig.StartAsync();
        var (libraryId, _) = await rig.AddRadarrFilmAsync();
        await ScanAsRadarrMatchedFileAsync(rig, libraryId);
        var replies = RedownloadRiskReplies.For();
        rig.Manager.Json(HttpMethod.Get, replies.FilePath, replies.FileJson).Json(HttpMethod.Get, replies.ProfilePath, replies.ProfileJson);

        var skipped = await CleanAsync(rig, libraryId);
        await SaveAsync(rig.Admin, libraryId, new { skip_if_manager_would_redownload = false });
        var cleaned = await CleanAsync(rig, libraryId);

        Assert.Equal(0, skipped["queued"]!.GetValue<int>());
        Assert.Equal([RedownloadRig.FilmPath], SkippedPaths(skipped));
        Assert.Equal(
            [$"Arrival.mkv: {RedownloadRiskReplies.MultiAudioWarning(RedownloadRig.RadarrName, "Arrival")}"],
            skipped["warnings"]!.AsArray().Select(warning => warning!.GetValue<string>()));
        Assert.Equal(1, cleaned["queued"]!.GetValue<int>());
        Assert.Empty(SkippedPaths(cleaned));
    }

    private static async Task<JsonNode> SaveAsync(ApiTestClient client, long libraryId, object changes)
    {
        var body = JsonNode.Parse(JsonSerializer.Serialize(changes))!.AsObject();
        body["library_folders"] = new JsonArray();
        body["csrf_token"] = await client.CsrfAsync();
        using var response = await client.PutAsync(SettingsPath(libraryId), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private static async Task<JsonNode> CleanAsync(RedownloadRig rig, long libraryId)
    {
        using var response = await rig.Admin.PostAsync(
            CleanPath(libraryId),
            new { csrf_token = await rig.Admin.CsrfAsync(), paths = new[] { RedownloadRig.FilmPath }, confirm_final_removal = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private static string[] SkippedPaths(JsonNode answer) =>
        [.. answer["skipped_paths"]!.AsArray().Select(path => path!.GetValue<string>())];

    // The scan has matched the film to its Radarr title and found a Japanese track the rules would remove.
    private static async Task ScanAsRadarrMatchedFileAsync(RedownloadRig rig, long libraryId)
    {
        var replies = RedownloadRiskReplies.For();
        await TestDatabase.ExecuteAsync(
            rig.Server,
            "UPDATE library_files SET classification = 'would_change', removed_audio_tracks = 1, manager_file_id = $file, manager_quality_profile_id = $profile " +
            "WHERE library_id = $library AND path = $path",
            ("$file", replies.FileId),
            ("$profile", replies.ProfileId),
            ("$library", libraryId),
            ("$path", RedownloadRig.FilmPath));
        var fileId = await TestDatabase.ScalarAsync(
            rig.Server,
            "SELECT id FROM library_files WHERE library_id = $library AND path = $path",
            ("$library", libraryId),
            ("$path", RedownloadRig.FilmPath));
        await TestDatabase.ExecuteAsync(
            rig.Server,
            "INSERT INTO library_file_probes (library_file_id, probe_json) VALUES ($file, $probe)",
            ("$file", fileId),
            ("$probe", EnglishAndJapanese));
    }
}
