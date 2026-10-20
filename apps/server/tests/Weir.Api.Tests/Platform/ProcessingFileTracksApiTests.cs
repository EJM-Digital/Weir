using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processes;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The track list of a file and the manual choice made from it, answered from what the real ffprobe printed for real files
/// (<c>fixtures/recorded/ffprobe</c>): every kind of stream, who may ask, and each reason a request is turned away.
/// </summary>
public sealed class ProcessingFileTracksApiTests
{
    private const string Mkv = "mkv-with-subtitles-and-font.json";
    private const string Mp4 = "mp4-with-cover-and-timecode.json";

    private static string Recording(string name) => File.ReadAllText(Path.Join(AppContext.BaseDirectory, "fixtures", "recorded", "ffprobe", name));

    private static string TracksPath(long fileId) => $"/api/v1/processing/files/{fileId}/tracks";

    private static string ManualPlanPath(long fileId) => $"/api/v1/processing/files/{fileId}/manual-plan";

    private sealed class Rig(WeirTestServer server, ApiTestClient client, long fileId, string sourcePath, ProbeRunner probe) : IAsyncDisposable
    {
        public WeirTestServer Server { get; } = server;

        public ApiTestClient Client { get; } = client;

        public long FileId { get; } = fileId;

        public string SourcePath { get; } = sourcePath;

        public ProbeRunner Probe { get; } = probe;

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    /// <summary>Answers ffprobe with a recording (or a failure) and runs <see cref="OnProbe"/> first, so a test can change the file the way another program would.</summary>
    private sealed class ProbeRunner : IProcessRunner
    {
        public string Output { get; set; } = string.Empty;

        public int ExitCode { get; set; }

        public string Error { get; set; } = string.Empty;

        public Action? OnProbe { get; set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            OnProbe?.Invoke();
            return Task.FromResult(new ProcessResult { ExitCode = ExitCode, Stdout = Encoding.UTF8.GetBytes(Output), Stderr = Encoding.UTF8.GetBytes(Error) });
        }
    }

    private static async Task<Rig> StartAsync(string recording, string status = "on_hold", string fileName = "episode.mkv", bool onDisk = true)
    {
        var probe = new ProbeRunner { Output = Recording(recording) };
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", ApiTestClient.Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")],
            configureServices: services =>
            {
                services.AddSingleton<IProcessRunner>(probe);
                services.AddSingleton<IMediaToolResolver>(new FixedMediaToolResolver());
            });
        await TestDatabase.SeedAdminAsync(server);
        var watched = Directory.CreateDirectory(Path.Join(server.Home, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(server.Home, "output")).FullName;
        var relative = $"Show (2020)/{fileName}";
        var source = Path.Join(watched, "Show (2020)", fileName);
        if (onDisk)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, new byte[2048]);
        }

        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder) VALUES ('Shows', 'tv', $watched, $output) RETURNING id",
            ("$watched", watched), ("$output", output));
        var fileId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, $status, CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", relative), ("$status", status));
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return new Rig(server, client, fileId, source, probe);
    }

    private static async Task<JsonArray> StreamsAsync(Rig rig)
    {
        using var response = await rig.Client.GetAsync(TracksPath(rig.FileId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestClient.Json(response))["streams"]!.AsArray();
    }

    private static JsonNode Stream(JsonArray streams, int index) => streams.Single(s => s!["index"]!.GetValue<int>() == index)!;

    private static async Task<HttpResponseMessage> ChooseAsync(Rig rig, object? keep, object? order, ApiTestClient? client = null)
    {
        client ??= rig.Client;
        return await client.PostAsync(ManualPlanPath(rig.FileId), new { csrf_token = await client.CsrfAsync(), keep, order });
    }

    private static Task<long> QueuedPassesAsync(Rig rig) =>
        TestDatabase.ScalarAsync(rig.Server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");

    private static object[] Keep(params (int Index, bool Default, bool Forced)[] tracks) =>
        [.. tracks.Select(t => new { index = t.Index, @default = t.Default, forced = t.Forced })];

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string detail)
    {
        using (response)
        {
            Assert.Equal((status, detail), (response.StatusCode, await ApiTestClient.Detail(response)));
        }
    }

    [Fact]
    public async Task A_file_with_every_usual_kind_of_stream_lists_each_one_with_what_the_rules_would_do()
    {
        await using var rig = await StartAsync(Mkv);

        var streams = await StreamsAsync(rig);

        Assert.Equal([0, 1, 2, 3, 4, 5], streams.Select(s => s!["index"]!.GetValue<int>()));
        Assert.Equal(["video", "audio", "audio", "subtitle", "subtitle", "attachment"], streams.Select(s => s!["type"]!.GetValue<string>()));
        var english = Stream(streams, 1);
        Assert.Equal(("eng", "Stereo", 1, true, false), (english["language"]!.GetValue<string>(), english["title"]!.GetValue<string>(), english["channels"]!.GetValue<int>(), english["default"]!.GetValue<bool>(), english["forced"]!.GetValue<bool>()));
        Assert.Equal("mp2", english["codec"]!.GetValue<string>());
        var forcedSubtitle = Stream(streams, 3);
        Assert.Equal(("eng", false, true), (forcedSubtitle["language"]!.GetValue<string>(), forcedSubtitle["default"]!.GetValue<bool>(), forcedSubtitle["forced"]!.GetValue<bool>()));
        Assert.Null(Stream(streams, 0)["channels"]);
        Assert.True(Stream(streams, 0)["rule_would_keep"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_list_names_the_file_its_workflow_and_the_state_the_file_was_in_when_it_was_read()
    {
        await using var rig = await StartAsync(Mkv);

        using var response = await rig.Client.GetAsync(TracksPath(rig.FileId));
        var body = await ApiTestClient.Json(response);

        Assert.Equal(("Show (2020)/episode.mkv", "tv", rig.FileId), (body["relative_path"]!.GetValue<string>(), body["media_scope"]!.GetValue<string>(), body["file_id"]!.GetValue<long>()));
        Assert.Equal(2048, body["source_fingerprint"]!["size_bytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_cover_picture_is_listed_as_an_image_and_a_stream_the_pass_ignores_says_so()
    {
        await using var rig = await StartAsync(Mp4, fileName: "episode.mp4");

        var streams = await StreamsAsync(rig);

        Assert.Equal([0, 1, 2, 3, 4], streams.Select(s => s!["index"]!.GetValue<int>()));
        var cover = Stream(streams, 4);
        Assert.Equal("image", cover["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(cover["rule_reason"]!.GetValue<string>()));
        var timecode = Stream(streams, 2);
        Assert.Equal("data", timecode["type"]!.GetValue<string>());
        Assert.False(timecode["rule_would_keep"]!.GetValue<bool>());
        Assert.Equal("This pass does not act on this stream type.", timecode["rule_reason"]!.GetValue<string>());
        Assert.Equal("video", Stream(streams, 0)["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stream_the_probe_leaves_without_a_number_or_a_kind_is_left_out_or_listed_as_far_as_it_is_known()
    {
        await using var rig = await StartAsync(Mkv);
        var probe = JsonNode.Parse(rig.Probe.Output)!;
        var probed = probe["streams"]!.AsArray();
        foreach (var unnumbered in new[] { 0, 2, 4, 5 })
        {
            probed[unnumbered]!.AsObject().Remove("index");
        }

        probed[3]!.AsObject().Remove("codec_type");
        probed[3]!.AsObject().Remove("codec_name");
        rig.Probe.Output = probe.ToJsonString();

        var streams = await StreamsAsync(rig);

        Assert.Equal([1, 3], streams.Select(s => s!["index"]!.GetValue<int>()));
        Assert.Equal(("audio", "other"), (Stream(streams, 1)["type"]!.GetValue<string>(), Stream(streams, 3)["type"]!.GetValue<string>()));
        Assert.Null(Stream(streams, 3)["codec"]);
    }

    [Fact]
    public async Task An_audio_stream_that_does_not_say_how_many_channels_it_has_is_listed_without_a_count_and_the_others_keep_theirs()
    {
        await using var rig = await StartAsync(Mkv);
        var probe = JsonNode.Parse(rig.Probe.Output)!;
        probe["streams"]![1]!.AsObject().Remove("channels");
        rig.Probe.Output = probe.ToJsonString();

        var streams = await StreamsAsync(rig);

        Assert.Null(Stream(streams, 1)["channels"]);
        Assert.Equal(1, Stream(streams, 2)["channels"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_viewer_may_read_the_list_but_nobody_signed_out_may()
    {
        await using var rig = await StartAsync(Mkv);
        await TestDatabase.SeedViewerAsync(rig.Server);
        var viewer = new ApiTestClient(rig.Server);
        await viewer.SignInAsync("bob", ApiTestClient.ViewerPassword);

        using var asViewer = await viewer.GetAsync(TracksPath(rig.FileId));
        using var anonymous = await new ApiTestClient(rig.Server).GetAsync(TracksPath(rig.FileId));

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Unauthorized), (asViewer.StatusCode, anonymous.StatusCode));
    }

    [Fact]
    public async Task Asking_for_the_tracks_of_a_file_weir_has_no_record_of_is_a_404_in_plain_words()
    {
        await using var rig = await StartAsync(Mkv);

        await AssertRefusedAsync(await rig.Client.GetAsync(TracksPath(rig.FileId + 100)), HttpStatusCode.NotFound, "Weir has no record of that file.");
    }

    [Fact]
    public async Task Asking_for_the_tracks_of_a_file_that_is_no_longer_in_the_watched_folder_is_a_404()
    {
        await using var rig = await StartAsync(Mkv, onDisk: false);

        await AssertRefusedAsync(
            await rig.Client.GetAsync(TracksPath(rig.FileId)), HttpStatusCode.NotFound, "Weir could not find this file under the saved watched folder.");
    }

    [Fact]
    public async Task A_file_the_probe_cannot_read_is_a_400_that_does_not_repeat_the_tools_own_words()
    {
        await using var rig = await StartAsync(Mkv);
        rig.Probe.ExitCode = 1;
        rig.Probe.Error = "moov atom not found at 0x7ffd1c2a";

        using var response = await rig.Client.GetAsync(TracksPath(rig.FileId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await ApiTestClient.Detail(response);
        Assert.DoesNotContain("0x7ffd1c2a", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("moov", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_goes_missing_while_it_is_being_read_is_a_400_that_names_no_path()
    {
        await using var rig = await StartAsync(Mkv);
        rig.Probe.OnProbe = () => File.Delete(rig.SourcePath);

        using var response = await rig.Client.GetAsync(TracksPath(rig.FileId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await ApiTestClient.Detail(response);
        Assert.Equal("Weir could not read this file safely. Check that it is still in the watched folder, then try again.", detail);
    }

    [Fact]
    public async Task A_track_list_is_asked_for_by_a_whole_number()
    {
        await using var rig = await StartAsync(Mkv);

        using var response = await rig.Client.GetAsync("/api/v1/processing/files/not-a-number/tracks");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_manual_choice_is_queued_with_the_flags_and_order_it_was_made_with_and_noted_in_activity()
    {
        await using var rig = await StartAsync(Mkv);

        using var response = await ChooseAsync(rig, Keep((0, false, false), (1, true, false), (4, false, true), (3, false, false)), new[] { 0, 1, 4, 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestClient.Json(response);
        Assert.Equal("processing.file.remux_pass.v1", body["job_kind"]!.GetValue<string>());
        var payload = await TestDatabase.ScalarStringAsync(
            rig.Server, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", body["job_id"]!.GetValue<long>()));
        var manual = JsonNode.Parse(payload!)!["manual_plan"]!;
        Assert.Equal([0, 1, 4, 3], manual["order"]!.AsArray().Select(i => i!.GetValue<int>()));
        var kept = manual["keep"]!.AsArray();
        Assert.Equal((true, false), (kept[1]!["default"]!.GetValue<bool>(), kept[1]!["forced"]!.GetValue<bool>()));
        Assert.Equal((false, true), (kept[2]!["default"]!.GetValue<bool>(), kept[2]!["forced"]!.GetValue<bool>()));
        Assert.Equal(
            "alice chose 4 tracks to keep for Show (2020)/episode.mkv (job " + body["job_id"]!.GetValue<long>() + ").",
            await TestDatabase.ScalarStringAsync(rig.Server, "SELECT detail FROM activity_events WHERE event_type = 'processing.file_manual_plan_queued'"));
    }

    [Fact]
    public async Task A_manual_choice_is_for_operators_only_and_changes_nothing_when_turned_away()
    {
        await using var rig = await StartAsync(Mkv);
        await TestDatabase.SeedViewerAsync(rig.Server);
        var viewer = new ApiTestClient(rig.Server);
        await viewer.SignInAsync("bob", ApiTestClient.ViewerPassword);

        await AssertRefusedAsync(await ChooseAsync(rig, Keep((0, false, false), (1, true, false)), new[] { 0, 1 }, viewer), HttpStatusCode.Forbidden, "Forbidden.");
        using var anonymous = await ChooseAsync(rig, Keep((0, false, false), (1, true, false)), new[] { 0, 1 }, new ApiTestClient(rig.Server));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, await QueuedPassesAsync(rig));
    }

    [Fact]
    public async Task A_manual_choice_with_a_stale_confirmation_token_is_turned_away_in_plain_words()
    {
        await using var rig = await StartAsync(Mkv);

        using var response = await rig.Client.PostAsync(
            ManualPlanPath(rig.FileId), new { csrf_token = "not-the-session-token", keep = Keep((0, false, false), (1, true, false)), order = new[] { 0, 1 } });

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "Your confirmation token expired. Refresh the page and try again.");
        Assert.Equal(0, await QueuedPassesAsync(rig));
    }

    [Fact]
    public async Task A_manual_choice_that_leaves_out_the_keep_list_or_the_order_says_which()
    {
        await using var rig = await StartAsync(Mkv);
        var keep = Keep((0, false, false), (1, true, false));

        await AssertRefusedAsync(await ChooseAsync(rig, null, new[] { 0, 1 }), HttpStatusCode.UnprocessableEntity, "'keep' is required and must be a list of {index, default, forced}.");
        await AssertRefusedAsync(await ChooseAsync(rig, "all", new[] { 0, 1 }), HttpStatusCode.UnprocessableEntity, "'keep' is required and must be a list of {index, default, forced}.");
        await AssertRefusedAsync(await ChooseAsync(rig, keep, null), HttpStatusCode.UnprocessableEntity, "'order' is required and must be a list of integers.");
    }

    [Fact]
    public async Task A_manual_choice_with_a_keep_entry_that_has_no_whole_number_index_says_so()
    {
        await using var rig = await StartAsync(Mkv);
        const string expected = "Each item in 'keep' must be an object with an integer 'index'.";

        await AssertRefusedAsync(await ChooseAsync(rig, new object[] { 0 }, new[] { 0 }), HttpStatusCode.UnprocessableEntity, expected);
        await AssertRefusedAsync(await ChooseAsync(rig, new object[] { new { index = "one" } }, new[] { 0 }), HttpStatusCode.UnprocessableEntity, expected);
        await AssertRefusedAsync(await ChooseAsync(rig, new object[] { new { name = "video" } }, new[] { 0 }), HttpStatusCode.UnprocessableEntity, expected);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"keep everything\"")]
    [InlineData("null")]
    public async Task A_manual_choice_whose_body_is_not_an_object_is_a_422_and_queues_nothing(string body)
    {
        await using var rig = await StartAsync(Mkv);

        using var response = await rig.Client.SendAsync(HttpMethod.Post, ManualPlanPath(rig.FileId), content: TestDatabase.RawJson(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await QueuedPassesAsync(rig));
    }

    [Fact]
    public async Task A_manual_choice_with_an_order_entry_that_is_not_a_whole_number_says_so()
    {
        await using var rig = await StartAsync(Mkv);

        await AssertRefusedAsync(
            await ChooseAsync(rig, Keep((0, false, false), (1, true, false)), new object[] { 0, "1" }), HttpStatusCode.UnprocessableEntity, "'order' must be a list of integers.");
    }

    [Fact]
    public async Task A_manual_choice_for_a_file_that_is_not_on_hold_is_a_400_and_queues_nothing()
    {
        await using var rig = await StartAsync(Mkv, status: "rejected");

        await AssertRefusedAsync(
            await ChooseAsync(rig, Keep((0, false, false), (1, true, false)), new[] { 0, 1 }),
            HttpStatusCode.BadRequest,
            "This file is not on hold, so a manual track choice does not apply. Refresh Activity and try again.");
        Assert.Equal(0, await QueuedPassesAsync(rig));
    }

    [Fact]
    public async Task A_manual_choice_for_a_file_weir_has_no_record_of_or_cannot_find_is_a_404()
    {
        await using var rig = await StartAsync(Mkv);
        var keep = Keep((0, false, false), (1, true, false));

        using var unknown = await rig.Client.PostAsync(ManualPlanPath(rig.FileId + 100), new { csrf_token = await rig.Client.CsrfAsync(), keep, order = new[] { 0, 1 } });
        File.Delete(rig.SourcePath);

        await AssertRefusedAsync(unknown, HttpStatusCode.NotFound, "Weir has no record of that file.");
        await AssertRefusedAsync(
            await ChooseAsync(rig, keep, new[] { 0, 1 }), HttpStatusCode.NotFound, "Weir could not find this file under the saved watched folder.");
    }

    [Theory]
    [InlineData("none", "At least one track must be kept.")]
    [InlineData("twice", "Stream 1 is listed more than once.")]
    [InlineData("unknown", "Stream 99 does not exist on this file.")]
    [InlineData("no-video", "At least one video track must be kept.")]
    [InlineData("no-audio", "At least one audio track must be kept.")]
    [InlineData("two-audio-defaults", "Only one audio track can be set as default.")]
    [InlineData("two-subtitle-defaults", "Only one subtitle track can be set as default.")]
    [InlineData("short-order", "The track order must list every kept track exactly once.")]
    [InlineData("repeated-order", "The track order must list every kept track exactly once.")]
    [InlineData("foreign-order", "The track order must list every kept track exactly once.")]
    public async Task A_manual_choice_that_breaks_a_rule_of_the_pass_is_a_400_that_says_which_rule(string case_, string detail)
    {
        await using var rig = await StartAsync(Mkv);
        var (keep, order) = case_ switch
        {
            "none" => (Keep(), Array.Empty<int>()),
            "twice" => (Keep((0, false, false), (1, true, false), (1, false, false)), new[] { 0, 1, 1 }),
            "unknown" => (Keep((0, false, false), (1, true, false), (99, false, false)), new[] { 0, 1, 99 }),
            "no-video" => (Keep((1, true, false)), new[] { 1 }),
            "no-audio" => (Keep((0, false, false), (3, false, false)), new[] { 0, 3 }),
            "two-audio-defaults" => (Keep((0, false, false), (1, true, false), (2, true, false)), new[] { 0, 1, 2 }),
            "two-subtitle-defaults" => (Keep((0, false, false), (1, true, false), (3, true, false), (4, true, false)), new[] { 0, 1, 3, 4 }),
            "short-order" => (Keep((0, false, false), (1, true, false)), new[] { 0 }),
            "repeated-order" => (Keep((0, false, false), (1, true, false)), new[] { 0, 0 }),
            _ => (Keep((0, false, false), (1, true, false)), new[] { 0, 2 }),
        };

        await AssertRefusedAsync(await ChooseAsync(rig, keep, order), HttpStatusCode.BadRequest, detail);
        Assert.Equal(0, await QueuedPassesAsync(rig));
    }

    [Fact]
    public async Task A_manual_choice_for_a_file_that_goes_missing_while_it_is_being_read_is_a_400_that_names_no_path()
    {
        await using var rig = await StartAsync(Mkv);
        rig.Probe.OnProbe = () => File.Delete(rig.SourcePath);

        await AssertRefusedAsync(
            await ChooseAsync(rig, Keep((0, false, false), (1, true, false)), new[] { 0, 1 }),
            HttpStatusCode.BadRequest,
            "Weir could not read this file safely. Check that it is still in the watched folder, then try again.");
        Assert.Equal(0, await QueuedPassesAsync(rig));
    }
}
