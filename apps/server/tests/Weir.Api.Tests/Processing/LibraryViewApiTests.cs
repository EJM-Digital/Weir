using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Processing;

/// <summary>
/// Issue #568's Library view over real HTTP: the Overview's totals and breakdowns, the Files table's paging,
/// sorting and facet filters, and the Problems grouping. The scan index is seeded straight into
/// <c>library_files</c> (no scan job runs here) because these endpoints' whole job is reading it.
/// </summary>
public sealed class LibraryViewApiTests
{
    internal const string FilmProbe = """
    {"streams": [
      {"index": 0, "codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160},
      {"index": 1, "codec_type": "audio", "codec_name": "eac3", "channels": 6, "channel_layout": "5.1(side)", "tags": {"language": "eng"}},
      {"index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": {"language": "jpn"}},
      {"index": 3, "codec_type": "subtitle", "codec_name": "subrip", "tags": {"language": "eng"}}
    ]}
    """;

    internal const string ShowProbe = """
    {"streams": [
      {"index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080},
      {"index": 1, "codec_type": "audio", "codec_name": "ac3", "channels": 6, "tags": {"language": "eng"}}
    ]}
    """;

    /// <summary>A show whose one audio track is already marked default, as a file that has been through Weir is.</summary>
    private const string DefaultAudioShowProbe = """
    {"streams": [
      {"index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080},
      {"index": 1, "codec_type": "audio", "codec_name": "ac3", "channels": 6, "tags": {"language": "eng"}, "disposition": {"default": 1}}
    ]}
    """;

    private static string OverviewPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-overview";

    private static string ProblemsPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-problems";

    internal static string FilesPath(long libraryId, string query = "") =>
        $"/api/v1/processing/libraries/{libraryId}/library-files" + (query.Length > 0 ? "?" + query : "");

    internal static async Task<(WeirTestServer Server, ApiTestClient Client, long LibraryId)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, display_order) " +
            "VALUES ('Library view', 'movie', '/in', '/out', '/work', 9) RETURNING id");
        return (server, client, libraryId);
    }

    /// <summary>
    /// Seeds a scanned file with its derived facts, exactly as <c>LibraryScanStore</c> writes them — the facet
    /// rows included, since the view groups by those.
    /// </summary>
    internal static async Task SeedFileAsync(
        WeirTestServer server,
        long libraryId,
        string path,
        string classification,
        string probeJson,
        long sizeBytes = 1_000_000,
        int? linkCount = null,
        string? problemKind = null,
        string? reason = null)
    {
        var facts = Core.LibraryMode.LibraryFileFactsReader.Derive(probeJson);
        var fileId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO library_files (library_id, path, size_bytes, mtime, classification, reason, removed_audio_tracks, " +
            "removed_subtitle_tracks, estimated_bytes_saved, video_codec, video_height, resolution_class, " +
            "audio_track_count, subtitle_track_count, audio_summary, subtitle_summary, link_count, problem_kind) VALUES " +
            "($library, $path, $size, 1700000000, $classification, $reason, 0, 0, 0, $codec, $height, $resolution, " +
            "$audio, $subtitle, $audio_summary, $subtitle_summary, $links, $problem) RETURNING id",
            ("$library", libraryId),
            ("$path", path),
            ("$size", sizeBytes),
            ("$classification", classification),
            ("$reason", (object?)reason ?? DBNull.Value),
            ("$codec", facts.VideoCodec),
            ("$height", (object?)facts.VideoHeight ?? DBNull.Value),
            ("$resolution", facts.ResolutionClass),
            ("$audio", facts.AudioTrackCount),
            ("$subtitle", facts.SubtitleTrackCount),
            ("$audio_summary", (object?)facts.AudioSummary ?? DBNull.Value),
            ("$subtitle_summary", (object?)facts.SubtitleSummary ?? DBNull.Value),
            ("$links", (object?)linkCount ?? DBNull.Value),
            ("$problem", (object?)problemKind ?? DBNull.Value));
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO library_file_probes (library_file_id, probe_json) VALUES ($f, $probe)",
            ("$f", fileId),
            ("$probe", probeJson));

        foreach (var facet in facts.Facets)
        {
            await TestDatabase.ExecuteAsync(
                server,
                "INSERT OR IGNORE INTO library_file_facets (library_id, library_file_id, facet, value) VALUES ($l, $f, $facet, $value)",
                ("$l", libraryId),
                ("$f", fileId),
                ("$facet", facet.Facet),
                ("$value", facet.Value));
        }
    }

    private static string[] Paths(JsonNode body) =>
        body["files"]!.AsArray().Select(file => file!["path"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task The_overview_reports_totals_and_a_breakdown_with_each_values_share()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe, sizeBytes: 3_000);
        await SeedFileAsync(server, libraryId, "/lib/show.mkv", "matches", ShowProbe, sizeBytes: 1_000);

        using var response = await client.GetAsync(OverviewPath(libraryId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(2, body["totals"]!["files"]!.GetValue<int>());
        Assert.Equal(4_000, body["totals"]!["size_bytes"]!.GetValue<long>());
        Assert.Equal(1, body["totals"]!["would_change"]!.GetValue<int>());

        var codecs = body["breakdowns"]!["video_codec"]!.AsArray();
        Assert.Equal(["h264", "hevc"], codecs.Select(row => row!["value"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(codecs, row => Assert.Equal(0.5, row!["share"]!.GetValue<double>()));

        var languages = body["breakdowns"]!["audio_language"]!.AsArray();
        var english = languages.Single(row => row!["value"]!.GetValue<string>() == "eng");
        Assert.Equal(2, english!["files"]!.GetValue<int>());
        Assert.Equal(1.0, english["share"]!.GetValue<double>());
        Assert.Contains(languages, row => row!["value"]!.GetValue<string>() == "jpn");
    }

    [Fact]
    public async Task An_unscanned_library_answers_with_empty_totals_and_no_scan()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;

        using var response = await client.GetAsync(OverviewPath(libraryId));

        var body = await Json(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body["totals"]!["files"]!.GetValue<int>());
        Assert.Equal(0, body["folders_configured"]!.GetValue<int>());
        Assert.Null(body["scan"]);
        Assert.Empty(body["breakdowns"]!["video_codec"]!.AsArray());
        Assert.Empty(body["problems"]!.AsArray());
    }

    [Fact]
    public async Task The_files_table_pages_and_sorts_by_a_named_column()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/a.mkv", "matches", ShowProbe, sizeBytes: 300);
        await SeedFileAsync(server, libraryId, "/lib/b.mkv", "matches", ShowProbe, sizeBytes: 100);
        await SeedFileAsync(server, libraryId, "/lib/c.mkv", "matches", ShowProbe, sizeBytes: 200);

        using var first = await client.GetAsync(FilesPath(libraryId, "sort=size&direction=desc&page_size=2"));
        using var second = await client.GetAsync(FilesPath(libraryId, "sort=size&direction=desc&page_size=2&page=2"));

        var firstBody = await Json(first);
        Assert.Equal(["/lib/a.mkv", "/lib/c.mkv"], Paths(firstBody));
        Assert.Equal(3, firstBody["total"]!.GetValue<int>());
        Assert.Equal(1, firstBody["page"]!.GetValue<int>());
        Assert.Equal("size", firstBody["sort"]!.GetValue<string>());
        Assert.Equal("desc", firstBody["direction"]!.GetValue<string>());
        Assert.Equal(["/lib/b.mkv"], Paths(await Json(second)));
    }

    [Fact]
    public async Task A_facet_filter_narrows_the_files_table_while_the_header_totals_stay_whole_library()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe, sizeBytes: 3_000);
        await SeedFileAsync(server, libraryId, "/lib/show.mkv", "matches", ShowProbe, sizeBytes: 1_000);

        using var response = await client.GetAsync(FilesPath(libraryId, "audio_language=jpn"));

        var body = await Json(response);
        Assert.Equal(["/lib/film.mkv"], Paths(body));
        Assert.Equal(1, body["total"]!.GetValue<int>());
        Assert.Equal(3_000, body["filtered"]!["size_bytes"]!.GetValue<long>());
        Assert.Equal(2, body["summary"]!["files"]!.GetValue<int>());
        Assert.Equal(4_000, body["summary"]!["size_bytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task Two_facet_filters_are_both_required_and_an_unknown_one_is_ignored()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "matches", FilmProbe);
        await SeedFileAsync(server, libraryId, "/lib/show.mkv", "matches", ShowProbe);

        using var both = await client.GetAsync(FilesPath(libraryId, "resolution=4k&video_codec=hevc"));
        using var contradictory = await client.GetAsync(FilesPath(libraryId, "resolution=4k&video_codec=h264"));
        using var unknown = await client.GetAsync(FilesPath(libraryId, "not_a_facet=hevc"));

        Assert.Equal(["/lib/film.mkv"], Paths(await Json(both)));
        Assert.Empty(Paths(await Json(contradictory)));
        Assert.Equal(2, (await Json(unknown))["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_file_row_carries_the_media_facts_the_table_shows()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);

        using var response = await client.GetAsync(FilesPath(libraryId));

        var file = (await Json(response))["files"]!.AsArray().Single()!;
        Assert.Equal("hevc", file["video_codec"]!.GetValue<string>());
        Assert.Equal("4k", file["resolution_class"]!.GetValue<string>());
        Assert.Equal(2, file["audio_track_count"]!.GetValue<int>());
        Assert.Equal("eng eac3 5.1, jpn aac stereo", file["audio_summary"]!.GetValue<string>());
        Assert.Equal("eng", file["subtitle_summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_file_with_no_cached_probe_reads_unknown_rather_than_being_rescanned()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/broken.mkv", "cannot_process", string.Empty, problemKind: "unreadable");

        using var response = await client.GetAsync(FilesPath(libraryId));

        var file = (await Json(response))["files"]!.AsArray().Single()!;
        Assert.Equal("unknown", file["video_codec"]!.GetValue<string>());
        Assert.Equal("unknown", file["resolution_class"]!.GetValue<string>());
        Assert.Null(file["audio_summary"]);
        Assert.Equal(0, file["audio_track_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Problems_group_by_reason_and_say_what_to_do_about_each()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/fine.mkv", "would_change", FilmProbe);
        await SeedFileAsync(server, libraryId, "/lib/seeding.mkv", "would_change", ShowProbe, linkCount: 2);
        await SeedFileAsync(
            server, libraryId, "/lib/broken.mkv", "cannot_process", string.Empty,
            problemKind: "unreadable", reason: "Weir could not read this file: ffprobe exited 1");

        using var response = await client.GetAsync(ProblemsPath(libraryId));

        var body = await Json(response);
        var groups = body["groups"]!.AsArray();
        Assert.Equal(["seeding", "unreadable"], groups.Select(g => g!["kind"]!.GetValue<string>()));
        Assert.Equal(2, body["total"]!.GetValue<int>());
        var seeding = groups[0]!;
        Assert.Equal(1, seeding["files"]!.GetValue<int>());
        Assert.Equal(["/lib/seeding.mkv"], seeding["sample_paths"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Contains("seeding finishes", seeding["what_to_do"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEmpty(seeding["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_problem_group_can_be_opened_as_a_files_filter()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/fine.mkv", "would_change", FilmProbe);
        await SeedFileAsync(server, libraryId, "/lib/seeding.mkv", "would_change", ShowProbe, linkCount: 4);

        using var response = await client.GetAsync(FilesPath(libraryId, "problem=seeding"));

        Assert.Equal(["/lib/seeding.mkv"], Paths(await Json(response)));
    }

    [Fact]
    public async Task A_library_that_allows_hardlinked_files_stops_reporting_seeding_as_a_problem()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/seeding.mkv", "would_change", ShowProbe, linkCount: 2);

        using var before = await client.GetAsync(ProblemsPath(libraryId));
        Assert.Single((await Json(before))["groups"]!.AsArray());

        using var saved = await client.PutAsync(
            $"/api/v1/processing/libraries/{libraryId}/library-settings",
            new { library_folders = Array.Empty<string>(), clean_hardlinked_files = true, csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var after = await client.GetAsync(ProblemsPath(libraryId));
        Assert.Empty((await Json(after))["groups"]!.AsArray());
    }

    [Fact]
    public async Task The_new_views_need_a_signed_in_user_and_a_library_that_exists()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var anonymous = new ApiTestClient(server);

        foreach (var path in new[] { OverviewPath(libraryId), ProblemsPath(libraryId) })
        {
            using var unauthenticated = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        }

        var missing = libraryId + 5_000;
        using var notFound = await client.GetAsync(OverviewPath(missing));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        using var problemsNotFound = await client.GetAsync(ProblemsPath(missing));
        Assert.Equal(HttpStatusCode.NotFound, problemsNotFound.StatusCode);
    }

    internal static string CleanPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-files/clean";

    internal static string LeaveAlonePath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-files/leave-alone";

    /// <summary>Keep the video and the second audio track: the opposite of what the seeded library's rules would do.</summary>
    internal static object JapaneseOnlyChoice() => new
    {
        keep = new[]
        {
            new { index = 0, @default = false, forced = false },
            new { index = 2, @default = true, forced = false },
        },
        order = new[] { 0, 2 },
    };

    [Fact]
    public async Task A_file_left_alone_stays_left_alone_when_it_is_selected_for_cleaning()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);

        using var set = await client.PostAsync(
            LeaveAlonePath(libraryId),
            new { csrf_token = await client.CsrfAsync(), path = "/lib/film.mkv", leave_alone = true });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using var clean = await client.PostAsync(
            CleanPath(libraryId),
            new { csrf_token = await client.CsrfAsync(), paths = new[] { "/lib/film.mkv" }, confirm_final_removal = true });

        var body = await Json(clean);
        Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
        Assert.Equal(0, body["queued"]!.GetValue<int>());
        Assert.Equal(["/lib/film.mkv"], body["skipped_paths"]!.AsArray().Select(p => p!.GetValue<string>()));
        // Nothing will be removed, so nothing claims it will be.
        Assert.Equal(0, body["files_count"]!.GetValue<int>());
        Assert.Equal(0, body["tracks_count"]!.GetValue<int>());

        // And the table says so, both as a count and as something to filter by.
        using var files = await client.GetAsync(FilesPath(libraryId, "state=left_alone"));
        var table = await Json(files);
        Assert.Equal(["/lib/film.mkv"], Paths(table));
        Assert.True(table["files"]![0]!["leave_alone"]!.GetValue<bool>());
        Assert.Equal(1, table["summary"]!["left_alone"]!.GetValue<int>());
        Assert.Equal(0, table["summary"]!["cleaned"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_file_left_alone_is_not_counted_in_what_the_confirmation_asks_about()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);
        await SeedFileAsync(server, libraryId, "/lib/show.mkv", "would_change", ShowProbe);
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE library_files SET removed_audio_tracks = 1 WHERE library_id = $l",
            ("$l", libraryId));

        using var set = await client.PostAsync(
            LeaveAlonePath(libraryId),
            new { csrf_token = await client.CsrfAsync(), path = "/lib/film.mkv", leave_alone = true });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using var clean = await client.PostAsync(
            CleanPath(libraryId),
            new { csrf_token = await client.CsrfAsync(), paths = new[] { "/lib/film.mkv", "/lib/show.mkv" } });

        // One of the two is set aside, so the dialog asks about the other one only.
        var body = await Json(clean);
        Assert.Equal(HttpStatusCode.BadRequest, clean.StatusCode);
        Assert.Equal(1, body["files_count"]!.GetValue<int>());
        Assert.Equal(1, body["tracks_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Changing_your_mind_about_a_file_lets_it_be_cleaned_again()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);

        foreach (var leaveAlone in new[] { true, false })
        {
            using var set = await client.PostAsync(
                LeaveAlonePath(libraryId),
                new { csrf_token = await client.CsrfAsync(), path = "/lib/film.mkv", leave_alone = leaveAlone });
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        using var clean = await client.PostAsync(
            CleanPath(libraryId),
            new { csrf_token = await client.CsrfAsync(), paths = new[] { "/lib/film.mkv" }, confirm_final_removal = true });

        var body = await Json(clean);
        Assert.Equal(1, body["queued"]!.GetValue<int>());
        Assert.Empty(body["skipped_paths"]!.AsArray());
    }

    [Fact]
    public async Task A_track_choice_is_quoted_back_for_confirmation_before_anything_is_queued()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        // "matches": the library's own rules would leave this file alone, so only the choice can explain the removal.
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "matches", FilmProbe);

        using var response = await client.PostAsync(
            CleanPath(libraryId),
            new { csrf_token = await client.CsrfAsync(), paths = new[] { "/lib/film.mkv" }, manual_plan = JapaneseOnlyChoice() });

        var body = await Json(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, body["files_count"]!.GetValue<int>());
        // Dropping the English audio track and the subtitle track the choice does not keep.
        Assert.Equal(2, body["tracks_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_confirmed_track_choice_queues_a_clean_for_a_file_the_rules_are_happy_with()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "matches", FilmProbe);

        using var response = await client.PostAsync(
            CleanPath(libraryId),
            new
            {
                csrf_token = await client.CsrfAsync(),
                paths = new[] { "/lib/film.mkv" },
                confirm_final_removal = true,
                manual_plan = JapaneseOnlyChoice(),
            });

        var body = await Json(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body["queued"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_choice_that_marks_no_default_audio_on_a_file_with_none_is_queued_to_give_it_one()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/show.mkv", "matches", ShowProbe);

        using var response = await client.PostAsync(
            CleanPath(libraryId),
            new
            {
                csrf_token = await client.CsrfAsync(),
                paths = new[] { "/lib/show.mkv" },
                confirm_final_removal = true,
                manual_plan = new { keep = new[] { new { index = 0, @default = false, forced = false }, new { index = 1, @default = false, forced = false } }, order = new[] { 0, 1 } },
            });

        var body = await Json(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body["queued"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_track_choice_that_cannot_apply_is_refused_rather_than_queued()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/film.mkv", "would_change", FilmProbe);
        await SeedFileAsync(server, libraryId, "/lib/show.mkv", "would_change", ShowProbe);
        await SeedFileAsync(server, libraryId, "/lib/settled.mkv", "would_change", DefaultAudioShowProbe);
        var csrf = await client.CsrfAsync();

        // One file at a time: track numbers mean nothing across two files.
        using var twoFiles = await client.PostAsync(
            CleanPath(libraryId),
            new { csrf_token = csrf, paths = new[] { "/lib/film.mkv", "/lib/show.mkv" }, confirm_final_removal = true, manual_plan = JapaneseOnlyChoice() });
        Assert.Equal(HttpStatusCode.BadRequest, twoFiles.StatusCode);

        // A track the file does not have.
        using var noSuchTrack = await client.PostAsync(
            CleanPath(libraryId),
            new
            {
                csrf_token = csrf,
                paths = new[] { "/lib/show.mkv" },
                confirm_final_removal = true,
                manual_plan = new { keep = new[] { new { index = 0, @default = false, forced = false }, new { index = 7, @default = true, forced = false } }, order = new[] { 0, 7 } },
            });
        Assert.Equal(HttpStatusCode.BadRequest, noSuchTrack.StatusCode);

        // Keeping everything the file already holds, flags and all, is not a clean.
        using var nothingToDo = await client.PostAsync(
            CleanPath(libraryId),
            new
            {
                csrf_token = csrf,
                paths = new[] { "/lib/settled.mkv" },
                confirm_final_removal = true,
                manual_plan = new { keep = new[] { new { index = 0, @default = false, forced = false }, new { index = 1, @default = true, forced = false } }, order = new[] { 0, 1 } },
            });
        Assert.Equal(HttpStatusCode.BadRequest, nothingToDo.StatusCode);

        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.library.clean.v1'"));
    }

    [Fact]
    public async Task A_page_size_beyond_the_cap_is_clamped_rather_than_refused()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/a.mkv", "matches", ShowProbe);

        using var response = await client.GetAsync(FilesPath(libraryId, "page_size=100000&page=0"));

        var body = await Json(response);
        Assert.Equal(
            Infrastructure.LibraryMode.LibraryFileSort.MaxPageSize.ToString(CultureInfo.InvariantCulture),
            body["page_size"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture));
        Assert.Equal(1, body["page"]!.GetValue<int>());
    }

    private static Task<long> SeedScanJobAsync(WeirTestServer server, long libraryId, string status, string payloadJson) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ($dedupe, 'processing.library.scan.v1', $payload, $status) RETURNING id",
            ("$dedupe", $"processing.library.scan.v1:{libraryId}:{Guid.NewGuid():N}"),
            ("$payload", payloadJson),
            ("$status", status));

    [Fact]
    public async Task Indexed_files_whose_scan_row_was_pruned_read_as_a_completed_scan_of_unknown_time()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/a.mkv", "matches", ShowProbe);

        using var response = await client.GetAsync(FilesPath(libraryId));

        var scan = (await Json(response))["scan"]!;
        Assert.Null(scan["job_id"]);
        Assert.Equal("completed", scan["status"]!.GetValue<string>());
        Assert.False(scan["running"]!.GetValue<bool>());
        Assert.Equal(0L, scan["generated_at"]!.GetValue<long>());
        Assert.Empty(scan["errors"]!.AsArray());
    }

    [Fact]
    public async Task The_latest_completed_scan_reports_its_time_and_what_it_could_not_do()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var jobId = await SeedScanJobAsync(
            server, libraryId, "completed", "{\"library_id\":1,\"ok\":true,\"scan_result\":{\"generated_at\":1700000000,\"errors\":[\"Radarr did not answer.\"]}}");

        using var response = await client.GetAsync(ProblemsPath(libraryId));

        var scan = (await Json(response))["scan"]!;
        Assert.Equal(jobId, scan["job_id"]!.GetValue<long>());
        Assert.Equal("completed", scan["status"]!.GetValue<string>());
        Assert.False(scan["running"]!.GetValue<bool>());
        Assert.Equal(1_700_000_000L, scan["generated_at"]!.GetValue<long>());
        Assert.Equal(["Radarr did not answer."], scan["errors"]!.AsArray().Select(error => error!.GetValue<string>()));
    }

    [Fact]
    public async Task A_queued_scan_over_an_existing_index_is_running_with_no_scan_time_yet()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedFileAsync(server, libraryId, "/lib/a.mkv", "matches", ShowProbe);
        await SeedScanJobAsync(server, libraryId, "completed", "{\"scan_result\":{\"generated_at\":1700000000,\"errors\":[]}}");
        var queued = await SeedScanJobAsync(server, libraryId, "pending", "{\"library_id\":1,\"trigger\":\"manual\"}");

        using var response = await client.GetAsync(OverviewPath(libraryId));

        var scan = (await Json(response))["scan"]!;
        Assert.Equal(queued, scan["job_id"]!.GetValue<long>());
        Assert.True(scan["running"]!.GetValue<bool>());
        Assert.Equal(0L, scan["generated_at"]!.GetValue<long>());
        Assert.Empty(scan["errors"]!.AsArray());
    }

    [Fact]
    public async Task A_first_scan_still_queued_has_no_scan_time_at_all()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SeedScanJobAsync(server, libraryId, "pending", "{\"library_id\":1,\"trigger\":\"manual\"}");

        using var response = await client.GetAsync(OverviewPath(libraryId));

        var scan = (await Json(response))["scan"]!;
        Assert.Equal("pending", scan["status"]!.GetValue<string>());
        Assert.Null(scan["generated_at"]);
        Assert.Empty(scan["errors"]!.AsArray());
    }
}
