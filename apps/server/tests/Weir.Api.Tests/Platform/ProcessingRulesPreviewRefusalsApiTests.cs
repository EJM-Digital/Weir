using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Core.MediaManagers;
using Weir.Core.Rules;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processes;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// "Try on a file" when something is wrong: who may ask, a request that cannot be read, a file or rule Weir cannot use, a second
/// preview while one is running, and what the original-language lookup adds when it is switched on.
/// </summary>
public sealed class ProcessingRulesPreviewRefusalsApiTests
{
    private const string GarbageProbeError =
        "Truncating packet of size 13344 to 7\n[matroska,webm @ 00000194accffe40] EBML header parsing failed\ngarbage.mkv: Invalid data found when processing input\n";

    private const string MissingFileProbeError = "missing.mkv: No such file or directory\n";

    private const string Film =
        """{"format":{"duration":"10.0"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"}},{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"}}]}""";

    private static string PreviewPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/preview";

    private sealed class Probe : IProcessRunner
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Output { get; set; } = Film;

        public int ExitCode { get; set; }

        public string Error { get; set; } = string.Empty;

        /// <summary>When set, a probe waits here until <see cref="Release"/>, so a test can hold one preview open.</summary>
        public bool Hold { get; set; }

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            if (Hold)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ProcessResult { ExitCode = ExitCode, Stdout = Encoding.UTF8.GetBytes(Output), Stderr = Encoding.UTF8.GetBytes(Error) };
        }
    }

    private sealed class FixedLookup(LookupResult? result, Exception? failure = null) : IOriginalLanguageLookup
    {
        public Task<LookupResult> LookupAsync(string mediaScope, long? libraryId, string relativeMediaPath, HandoffOrigin? origin, CancellationToken cancellationToken) =>
            failure is null ? Task.FromResult(result!) : Task.FromException<LookupResult>(failure);
    }

    private sealed class Rig(WeirTestServer server, ApiTestClient client, long libraryId, string watched, Probe probe) : IAsyncDisposable
    {
        public WeirTestServer Server { get; } = server;

        public ApiTestClient Client { get; } = client;

        public long LibraryId { get; } = libraryId;

        public string Watched { get; } = watched;

        public Probe Probe { get; } = probe;

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Directory.Delete(Path.GetDirectoryName(Watched)!, recursive: true);
        }

        public async Task<HttpResponseMessage> PreviewAsync(object? body = null, ApiTestClient? client = null)
        {
            client ??= Client;
            var fields = body is null ? new JsonObject() : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(body))!.AsObject();
            fields["csrf_token"] = await client.CsrfAsync();
            return await client.PostAsync(PreviewPath(LibraryId), fields);
        }
    }

    private static async Task<Rig> StartAsync(IOriginalLanguageLookup? lookup = null, bool libraryHasRules = false)
    {
        var probe = new Probe();
        var server = await WeirTestServer.StartAsync(
            [
                ("WEIR_SESSION_SECRET", ApiTestClient.Secret),
                ("WEIR_PROCESSING_WORKER_COUNT", "0"),
                ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_SCHEDULE_ENABLED", "false"),
            ],
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(probe));
                services.Replace(ServiceDescriptor.Singleton<IMediaToolResolver>(new FixedMediaToolResolver()));
                if (lookup is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(lookup));
                }
            });
        await TestDatabase.SeedAdminAsync(server);
        var root = Directory.CreateTempSubdirectory("weir-preview-refusals-").FullName;
        var watched = Directory.CreateDirectory(Path.Join(root, "watched")).FullName;
        var output = Directory.CreateDirectory(Path.Join(root, "output")).FullName;
        await File.WriteAllBytesAsync(Path.Join(watched, "film.mkv"), new byte[16]);
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder) VALUES ('Films', 'movie', $w, $o) RETURNING id",
            ("$w", watched), ("$o", output));
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return new Rig(server, client, libraryId, watched, probe);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string detail)
    {
        using (response)
        {
            Assert.Equal((status, detail), (response.StatusCode, await ApiTestClient.Detail(response)));
        }
    }

    [Fact]
    public async Task A_workflow_with_no_rules_of_its_own_is_previewed_with_the_shipped_defaults()
    {
        await using var rig = await StartAsync();

        using var response = await rig.PreviewAsync(new { relative_path = "film.mkv" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestClient.Json(response);
        Assert.Equal(("movie", Path.Join(rig.Watched, "film.mkv")), (body["media_scope"]!.GetValue<string>(), body["inspected_path"]!.GetValue<string>()));
        Assert.Equal(3, body["tracks"]!.AsArray().Count);
    }

    [Fact]
    public async Task Previewing_is_for_operators_and_changes_nothing()
    {
        await using var rig = await StartAsync();

        await OperatorPostRefusals.AssertAsync(rig.Server, PreviewPath(rig.LibraryId), token => new { csrf_token = token, relative_path = "film.mkv" });

        Assert.False(rig.Probe.Started.IsCompleted);
    }

    [Fact]
    public async Task A_workflow_that_does_not_exist_is_a_404()
    {
        await using var rig = await StartAsync();

        using var response = await rig.Client.PostAsync(PreviewPath(rig.LibraryId + 100), new { csrf_token = await rig.Client.CsrfAsync(), relative_path = "film.mkv" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rules_that_are_not_an_object_are_a_422_naming_the_field()
    {
        await using var rig = await StartAsync();

        using var response = await rig.PreviewAsync(new { relative_path = "film.mkv", rules = "keep everything" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var issue = (await ApiTestClient.Json(response))["detail"]![0]!;
        Assert.Equal("dict_type", issue["type"]!.GetValue<string>());
        Assert.Equal("rules", issue["loc"]![1]!.GetValue<string>());
    }

    [Fact]
    public async Task Rules_with_a_field_the_rule_sets_do_not_have_are_a_422_like_a_save_would_be()
    {
        await using var rig = await StartAsync();

        using var response = await rig.PreviewAsync(new { relative_path = "film.mkv", rules = new { name = "Try", make_it_louder = true } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("track_name_template")]
    [InlineData("audio_sorters_json")]
    public async Task Rules_that_cannot_be_applied_are_a_400_in_plain_words_and_nothing_is_probed(string field)
    {
        await using var rig = await StartAsync();
        var rules = new JsonObject { ["name"] = "Try", ["standardize_track_names"] = true, [field] = field == "track_name_template" ? "{nonsense}" : "not json at all" };

        using var response = await rig.PreviewAsync(new { relative_path = "film.mkv", rules });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ApiTestClient.Detail(response)));
        Assert.False(rig.Probe.Started.IsCompleted);
    }

    [Fact]
    public async Task A_path_in_neither_folder_or_in_both_or_none_is_a_400()
    {
        await using var rig = await StartAsync();

        await AssertRefusedAsync(
            await rig.PreviewAsync(new { relative_path = "../film.mkv" }),
            HttpStatusCode.BadRequest,
            "That path is not an existing file inside this workflow's watched or output folder.");
        await AssertRefusedAsync(
            await rig.PreviewAsync(new { relative_path = "film.mkv", absolute_path = Path.Join(rig.Watched, "film.mkv") }),
            HttpStatusCode.BadRequest,
            "Give either relative_path or absolute_path, not both or neither.");
        await AssertRefusedAsync(
            await rig.PreviewAsync(new { relative_path = "  " }),
            HttpStatusCode.BadRequest,
            "Give either relative_path or absolute_path, not both or neither.");
    }

    [Fact]
    public async Task A_file_in_the_output_folder_can_be_previewed_when_the_watched_folder_has_no_such_file()
    {
        await using var rig = await StartAsync();
        await File.WriteAllBytesAsync(Path.Join(Path.GetDirectoryName(rig.Watched)!, "output", "done.mkv"), new byte[16]);

        using var response = await rig.PreviewAsync(new { relative_path = "done.mkv" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_workflow_with_no_output_folder_cannot_preview_a_file_that_is_not_in_its_watched_folder()
    {
        await using var rig = await StartAsync();
        await TestDatabase.ExecuteAsync(rig.Server, "UPDATE libraries SET output_folder = '' WHERE id = $id", ("$id", rig.LibraryId));

        await AssertRefusedAsync(
            await rig.PreviewAsync(new { relative_path = "nothing.mkv" }),
            HttpStatusCode.BadRequest,
            "That path is not an existing file inside this workflow's watched or output folder.");
    }

    [Fact]
    public async Task An_absolute_path_that_is_not_a_file_or_not_allowed_is_refused_with_the_pickers_own_words()
    {
        await using var rig = await StartAsync();

        await AssertRefusedAsync(
            await rig.PreviewAsync(new { absolute_path = Path.Join(rig.Watched, "gone.mkv") }), HttpStatusCode.NotFound, "The requested file does not exist.");
        await AssertRefusedAsync(
            await rig.PreviewAsync(new { absolute_path = "//server/share/film.mkv" }), HttpStatusCode.BadRequest, "UNC and device paths are not allowed.");
    }

    [Fact]
    public async Task A_file_the_probe_calls_unreadable_is_a_400_that_does_not_repeat_the_tools_own_words()
    {
        await using var rig = await StartAsync();
        rig.Probe.ExitCode = 1;
        rig.Probe.Output = "{\n\n}\n";
        rig.Probe.Error = GarbageProbeError;

        using var response = await rig.PreviewAsync(new { relative_path = "film.mkv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await ApiTestClient.Detail(response);
        Assert.DoesNotContain("EBML", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("00000194", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_the_probe_fails_on_for_any_other_reason_is_a_400_that_says_what_to_check()
    {
        await using var rig = await StartAsync();
        rig.Probe.ExitCode = 1;
        rig.Probe.Output = "{\n\n}\n";
        rig.Probe.Error = MissingFileProbeError;

        await AssertRefusedAsync(
            await rig.PreviewAsync(new { relative_path = "film.mkv" }),
            HttpStatusCode.BadRequest,
            "Weir could not read this file. Check it is a complete media file that Weir can open.");
    }

    [Fact]
    public async Task A_file_with_no_video_cannot_be_previewed()
    {
        await using var rig = await StartAsync();
        rig.Probe.Output = """{"format":{"duration":"10.0"},"streams":[{"index":0,"codec_type":"audio","codec_name":"aac","tags":{"language":"eng"}}]}""";

        await AssertRefusedAsync(
            await rig.PreviewAsync(new { relative_path = "film.mkv" }),
            HttpStatusCode.BadRequest,
            "This file contains no video stream, so Weir cannot preview a plan for it.");
    }

    [Fact]
    public async Task A_file_with_no_audio_cannot_be_previewed()
    {
        await using var rig = await StartAsync();
        rig.Probe.Output = """{"format":{"duration":"10.0"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264"}]}""";

        await AssertRefusedAsync(
            await rig.PreviewAsync(new { relative_path = "film.mkv" }),
            HttpStatusCode.BadRequest,
            "No audio track would remain with these rules, so Weir cannot preview a plan for this file.");
    }

    [Fact]
    public async Task A_second_preview_while_one_is_running_is_told_to_try_again_and_the_first_still_finishes()
    {
        await using var rig = await StartAsync();
        rig.Probe.Hold = true;
        var first = rig.PreviewAsync(new { relative_path = "film.mkv" });
        await rig.Probe.Started;

        await AssertRefusedAsync(
            await rig.PreviewAsync(new { relative_path = "film.mkv" }),
            HttpStatusCode.Conflict,
            "Another rules preview is already running. Try again in a moment.");
        rig.Probe.Release();

        using var finished = await first;
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
    }

    [Fact]
    public async Task Original_language_rules_report_what_the_lookup_found_and_which_tracks_that_favours()
    {
        var lookup = new FixedLookup(new LookupResult { Status = LookupResult.StatusMatched, Metadata = new TitleMetadata { OriginalLanguage = "ja", Title = "Film" } });
        await using var rig = await StartAsync(lookup);

        using var response = await rig.PreviewAsync(new { relative_path = "film.mkv", rules = new { name = "Try", keep_original_language = true } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var original = (await ApiTestClient.Json(response))["original_language"]!;
        Assert.Equal(("matched", "ja"), (original["lookup_status"]!.GetValue<string>(), original["original_language"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(original["note"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_lookup_that_fails_is_shown_as_unreachable_and_the_preview_still_answers()
    {
        await using var rig = await StartAsync(new FixedLookup(null, new HttpRequestException("connection refused")));

        using var response = await rig.PreviewAsync(new { relative_path = "film.mkv", rules = new { name = "Try", keep_original_language = true } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var original = (await ApiTestClient.Json(response))["original_language"]!;
        Assert.Equal("unreachable", original["lookup_status"]!.GetValue<string>());
        Assert.Equal("Weir could not look up the original language just now.", original["lookup_detail"]!.GetValue<string>());
        Assert.Null(original["original_language"]);
    }

    [Fact]
    public async Task A_preview_without_original_language_rules_has_no_original_language_section()
    {
        await using var rig = await StartAsync(new FixedLookup(new LookupResult { Status = LookupResult.StatusNotConfigured }));

        using var response = await rig.PreviewAsync(new { relative_path = "film.mkv" });

        Assert.Null((await ApiTestClient.Json(response))["original_language"]);
    }
}
