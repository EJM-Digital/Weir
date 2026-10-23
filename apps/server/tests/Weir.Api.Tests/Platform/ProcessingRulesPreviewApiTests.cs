using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processes;

namespace Weir.Api.Tests.Platform;

/// <summary>A process runner standing in for ffprobe: every call answers the same canned probe JSON.</summary>
internal sealed class FakePreviewProbeRunner : IProcessRunner
{
    public string ProbeJson { get; set; } = """{"format":{"duration":"10.0"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264"}]}""";

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request.Argv);
        return Task.FromResult(new ProcessResult { ExitCode = 0, Stdout = Encoding.UTF8.GetBytes(ProbeJson), Stderr = [] });
    }
}

internal sealed class FakePreviewToolResolver : IMediaToolResolver
{
    public (string Ffprobe, string Ffmpeg) Resolve() => ("ffprobe", "ffmpeg");

    public string? ResolveMkvmerge() => null;
}

/// <summary>
/// HTTP-level proof of issue #502's preview endpoint: <c>POST /api/v1/processing/libraries/{id}/preview</c>
/// runs a real ffprobe + <c>PlanRemux</c> pass against a real file, with ffprobe itself faked so the
/// suite needs no real ffmpeg binaries. Reads only — every test also checks nothing was written.
/// </summary>
public sealed class ProcessingRulesPreviewApiTests
{
    private const string TwoAudioTracksProbe =
        """{"format":{"duration":"10.0"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"bit_rate":"128000","tags":{"language":"eng"}},{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"bit_rate":"192000","tags":{"language":"jpn"}}]}""";

    private static async Task<(WeirTestServer Server, FakePreviewProbeRunner Runner)> StartAsync()
    {
        var runner = new FakePreviewProbeRunner();
        var server = await WeirTestServer.StartAsync(
            // The watched-folder scan's own periodic scheduler (1s poll, RunAtStart) would otherwise
            // independently discover the same watched folder these tests write into and enqueue a real
            // scan, and the work-folder sweeps enqueue a job of their own just after start; either could
            // land between the job counts below.
            variables:
            [
                ("WEIR_SESSION_SECRET", ApiTestClient.Secret),
                ("WEIR_PROCESSING_WORKER_COUNT", "0"),
                ("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_SCHEDULE_ENABLED", "false"),
                ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_ENABLED", "0"),
                ("WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_TV_SCHEDULE_ENABLED", "0"),
            ],
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(runner));
                services.Replace(ServiceDescriptor.Singleton<IMediaToolResolver>(new FakePreviewToolResolver()));
            }).ConfigureAwait(false);
        return (server, runner);
    }

    /// <summary>
    /// Outside <see cref="WeirTestServer.Home"/> (Weir's data folder for this test run) deliberately: a
    /// library's watched/output folders may not be Weir's own data folder or a folder inside it.
    /// </summary>
    private static (string Watched, string Output) MakeLibraryFolders()
    {
        var root = Directory.CreateTempSubdirectory("weir-preview-test-").FullName;
        var watched = Path.Join(root, "watched");
        var output = Path.Join(root, "output");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(output);
        return (watched, output);
    }

    private static async Task<(long LibraryId, long RuleSetId)> SeedLibraryWithRuleSetAsync(
        ApiTestClient client, string watched, string output, string primaryAudioLang)
    {
        var ruleSet = await client.PostAsync(
            "/api/v1/processing/rule-sets",
            new { csrf_token = await client.CsrfAsync(), name = "Preview rules " + Guid.NewGuid().ToString("N"), primary_audio_lang = primaryAudioLang });
        Assert.Equal(HttpStatusCode.Created, ruleSet.StatusCode);
        var ruleSetId = (await ApiTestClient.Json(ruleSet))!["id"]!.GetValue<long>();

        var library = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "Preview library " + Guid.NewGuid().ToString("N"), media_type = "movie", watched_folder = watched, output_folder = output });
        Assert.Equal(HttpStatusCode.Created, library.StatusCode);
        var libraryBody = await ApiTestClient.Json(library);
        var libraryId = libraryBody!["id"]!.GetValue<long>();

        var linked = await client.PutAsync(
            $"/api/v1/processing/libraries/{libraryId}",
            new { csrf_token = await client.CsrfAsync(), name = libraryBody["name"]!.GetValue<string>(), media_type = "movie", watched_folder = watched, output_folder = output, rule_set_id = ruleSetId });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        return (libraryId, ruleSetId);
    }

    [Fact]
    public async Task Unsaved_rules_change_which_track_is_kept_and_the_database_stays_the_same()
    {
        var (server, runner) = await StartAsync();
        await using var _ = server;
        runner.ProbeJson = TwoAudioTracksProbe;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, ruleSetId) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "jpn");
        await File.WriteAllBytesAsync(Path.Join(watched, "movie.mkv"), new byte[16]);
        var ruleSetCountBefore = await TestDatabase.ScalarAsync(server, "SELECT COUNT(*) FROM rule_sets");

        using var savedResponse = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), relative_path = "movie.mkv" });
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var savedBody = await ApiTestClient.Json(savedResponse);
        var savedTracks = savedBody!["tracks"]!.AsArray();
        var savedWinner = savedTracks.Single(t => t!["type"]!.GetValue<string>() == "audio" && t["action"]!.GetValue<string>() == "keep");
        Assert.Equal("jpn", savedWinner!["language"]!.GetValue<string>());

        using var unsavedResponse = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new
            {
                csrf_token = await client.CsrfAsync(),
                relative_path = "movie.mkv",
                rules = new { name = "Unsaved override", primary_audio_lang = "eng" },
            });
        Assert.Equal(HttpStatusCode.OK, unsavedResponse.StatusCode);
        var unsavedBody = await ApiTestClient.Json(unsavedResponse);
        var unsavedTracks = unsavedBody!["tracks"]!.AsArray();
        var unsavedWinner = unsavedTracks.Single(t => t!["type"]!.GetValue<string>() == "audio" && t["action"]!.GetValue<string>() == "keep");
        Assert.Equal("eng", unsavedWinner!["language"]!.GetValue<string>());

        // The database was never touched: the saved rule set still says "jpn", and no new rule set was inserted.
        var storedPrimaryLang = await TestDatabase.ScalarStringAsync(server, "SELECT primary_audio_lang FROM rule_sets WHERE id = $id", ("$id", ruleSetId));
        Assert.Equal("jpn", storedPrimaryLang);
        var ruleSetCount = await TestDatabase.ScalarAsync(server, "SELECT COUNT(*) FROM rule_sets");
        Assert.Equal(ruleSetCountBefore, ruleSetCount);
    }

    [Fact]
    public async Task A_preview_can_run_a_saved_profile_other_than_the_workflows()
    {
        var (server, runner) = await StartAsync();
        await using var _ = server;
        runner.ProbeJson = TwoAudioTracksProbe;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, _) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "jpn");
        await File.WriteAllBytesAsync(Path.Join(watched, "movie.mkv"), new byte[16]);
        using var created = await client.PostAsync(
            "/api/v1/processing/rule-sets",
            new { csrf_token = await client.CsrfAsync(), name = "English profile", primary_audio_lang = "eng" });
        var englishProfileId = (await ApiTestClient.Json(created))["id"]!.GetValue<long>();

        using var response = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), relative_path = "movie.mkv", rule_set_id = englishProfileId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tracks = (await ApiTestClient.Json(response))["tracks"]!.AsArray();
        var kept = tracks.Single(t => t!["type"]!.GetValue<string>() == "audio" && t["action"]!.GetValue<string>() == "keep");
        Assert.Equal("eng", kept!["language"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_preview_with_a_profile_that_does_not_exist_is_refused_with_400()
    {
        var (server, runner) = await StartAsync();
        await using var _ = server;
        runner.ProbeJson = TwoAudioTracksProbe;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, _) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "jpn");
        await File.WriteAllBytesAsync(Path.Join(watched, "movie.mkv"), new byte[16]);

        using var response = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), relative_path = "movie.mkv", rule_set_id = 987654 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_relative_path_outside_the_watched_and_output_folders_is_refused_with_400()
    {
        var (server, _) = await StartAsync();
        await using var _ = server;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, _) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "eng");
        // Not written anywhere under the watched or output folder.
        await File.WriteAllBytesAsync(Path.Join(server.Home, "elsewhere.mkv"), new byte[16]);

        using var response = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), relative_path = "../elsewhere.mkv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_file_under_the_watched_folder_is_refused_with_400_not_500()
    {
        var (server, _) = await StartAsync();
        await using var _ = server;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, _) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "eng");

        using var response = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), relative_path = "does-not-exist.mkv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Neither_path_nor_both_paths_given_is_refused_with_400()
    {
        var (server, _) = await StartAsync();
        await using var _ = server;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, _) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "eng");

        using var neither = await client.PostAsync($"/api/v1/processing/libraries/{libraryId}/preview", new { csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        using var both = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), relative_path = "movie.mkv", absolute_path = Path.Join(watched, "movie.mkv") });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task A_preview_creates_no_job_rows_and_writes_nothing()
    {
        var (server, runner) = await StartAsync();
        await using var _ = server;
        // One audio track that already matches the saved rules exactly, so the plan needs no change.
        runner.ProbeJson =
            """{"format":{"duration":"10.0"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264"},{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"disposition":{"default":1},"tags":{"language":"eng"}}]}""";
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, _) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "eng");
        await File.WriteAllBytesAsync(Path.Join(watched, "movie.mkv"), new byte[16]);
        var jobsBefore = await TestDatabase.ScalarAsync(server, "SELECT COUNT(*) FROM jobs");
        var filesBefore = await TestDatabase.ScalarAsync(server, "SELECT COUNT(*) FROM files");

        using var response = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), relative_path = "movie.mkv" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestClient.Json(response);
        Assert.False(body!["remux_required"]!.GetValue<bool>());
        Assert.True(body["estimated_size_reduction_is_estimate"]!.GetValue<bool>());
        Assert.Equal(jobsBefore, await TestDatabase.ScalarAsync(server, "SELECT COUNT(*) FROM jobs"));
        Assert.Equal(filesBefore, await TestDatabase.ScalarAsync(server, "SELECT COUNT(*) FROM files"));
    }

    /// <summary>
    /// End to end for #495/#496/#497 together, through the real HTTP surface the web preview panel calls:
    /// unsaved rules with per_language audio, a variant-specific subtitle language and hearing-impaired
    /// removal all take effect in one preview response.
    /// </summary>
    [Fact]
    public async Task Preview_reflects_per_language_audio_a_named_variant_and_hearing_impaired_removal_together()
    {
        var (server, runner) = await StartAsync();
        await using var _ = server;
        runner.ProbeJson =
            """
            {"format":{"duration":"120.0"},"streams":[
              {"index":0,"codec_type":"video","codec_name":"h264"},
              {"index":1,"codec_type":"audio","codec_name":"dts","channels":6,"bit_rate":"1500000","tags":{"language":"jpn"},"disposition":{"default":1}},
              {"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"bit_rate":"128000","tags":{"language":"eng"}},
              {"index":3,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng","title":"English (SDH)"}},
              {"index":4,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"fre","title":"VFQ"}},
              {"index":5,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"fre","title":"VFF"}}
            ]}
            """;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, _) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "jpn");
        await File.WriteAllBytesAsync(Path.Join(watched, "movie.mkv"), new byte[16]);

        using var response = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new
            {
                csrf_token = await client.CsrfAsync(),
                relative_path = "movie.mkv",
                rules = new
                {
                    name = "Per-language + variant + SDH",
                    primary_audio_lang = "jpn",
                    secondary_audio_lang = "eng",
                    audio_keep_mode = "per_language",
                    subtitle_mode = "keep_listed",
                    subtitle_langs_csv = "eng,fre-CA",
                    remove_hearing_impaired_subs = true,
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestClient.Json(response);
        var tracks = body!["tracks"]!.AsArray();
        bool KeptAt(int index) => tracks.Single(t => t!["index"]!.GetValue<int>() == index)!["action"]!.GetValue<string>() == "keep";

        Assert.True(KeptAt(1), "the original Japanese track should be kept");
        Assert.True(KeptAt(2), "the English dub should also be kept under per_language");
        Assert.False(KeptAt(3), "the English (SDH) subtitle should be dropped outright");
        Assert.True(KeptAt(4), "the VFQ (French Canada) subtitle matches the fre-CA rule");
        Assert.False(KeptAt(5), "the VFF (French France) subtitle does not match the fre-CA rule");
    }

    [Fact]
    public async Task An_absolute_path_outside_the_library_folders_but_within_a_valid_root_is_accepted()
    {
        var (server, runner) = await StartAsync();
        await using var _ = server;
        runner.ProbeJson = TwoAudioTracksProbe;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        var (libraryId, _) = await SeedLibraryWithRuleSetAsync(client, watched, output, primaryAudioLang: "eng");
        var elsewhere = Path.Join(server.Home, "elsewhere.mkv");
        await File.WriteAllBytesAsync(elsewhere, new byte[16]);

        using var response = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), absolute_path = elsewhere });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("tv", "   ")]
    [InlineData("movie", "   ")]
    public async Task A_workflow_of_either_kind_previews_unsaved_rules_whose_name_is_blank(string mediaType, string name)
    {
        var (server, runner) = await StartAsync();
        await using var _ = server;
        runner.ProbeJson = TwoAudioTracksProbe;
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var (watched, output) = MakeLibraryFolders();
        using var created = await client.PostAsync(
            "/api/v1/processing/libraries",
            new { csrf_token = await client.CsrfAsync(), name = "Preview " + mediaType, media_type = mediaType, watched_folder = watched, output_folder = output });
        var libraryId = (await ApiTestClient.Json(created))!["id"]!.GetValue<long>();
        await File.WriteAllBytesAsync(Path.Join(watched, "episode.mkv"), new byte[16]);

        using var preview = await client.PostAsync(
            $"/api/v1/processing/libraries/{libraryId}/preview",
            new { csrf_token = await client.CsrfAsync(), relative_path = "episode.mkv", rules = new { name, primary_audio_lang = "eng" } });

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }
}
