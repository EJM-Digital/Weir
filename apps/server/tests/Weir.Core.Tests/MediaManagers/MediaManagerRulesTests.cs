using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// Hand-off paths, manager dialects, completion callback bodies, and the pure intake and ledger rules.
/// </summary>
public sealed class MediaManagerRulesTests
{
    private static WireObject Dict(string json) => (WireObject)WireJsonParser.Parse(json);

    // --- hand-off paths ------------------------------------------------------------------------

    [Theory]
    [InlineData("/srv/handoff", "/srv/handoff/Film/film.mkv", "Film/film.mkv")]
    [InlineData("/srv/handoff/", "/srv/handoff/film.mkv", "film.mkv")]
    [InlineData("D:\\Handoff", "D:\\Handoff\\Film\\film.mkv", "Film/film.mkv")]
    [InlineData("D:/Handoff", "D:\\Handoff\\Film\\film.mkv", "Film/film.mkv")]
    [InlineData("D:\\handoff", "D:\\HANDOFF\\Film\\film.mkv", "Film/film.mkv")]
    [InlineData(@"\\media-nas\Data\Media\Handoff", @"\\media-nas\data\media\handoff\a\b.mkv", "a/b.mkv")]
    [InlineData("/w", "/w/a/b/c/d.mkv", "a/b/c/d.mkv")]
    public void Paths_inside_the_watched_folder_resolve(string watched, string file, string expected)
    {
        var result = HandoffPaths.RelativeMediaPathForHandoff(watched, file);
        Assert.True(result.Ok);
        Assert.Equal(expected, result.RelativeMediaPath);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void The_original_case_is_preserved_in_the_relative_path() =>
        Assert.Equal("Blade.Runner.2049/Film.MKV", HandoffPaths.RelativeMediaPathForHandoff("/srv/handoff", "/SRV/HANDOFF/Blade.Runner.2049/Film.MKV").RelativeMediaPath);

    [Theory]
    [InlineData("/srv/handoff", "/somewhere/else/film.mkv")]
    [InlineData("/srv/handoff", "/srv/handoff-other/film.mkv")]
    [InlineData("/srv/handoff", "/srv/handoff")]
    [InlineData("/srv/handoff", "/srv")]
    [InlineData("/srv/handoff", "/srv/handoff/../../etc/passwd")]
    public void Paths_outside_the_watched_folder_are_refused(string watched, string file)
    {
        var result = HandoffPaths.RelativeMediaPathForHandoff(watched, file);
        Assert.False(result.Ok);
        Assert.Contains("watched folder", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_folder_or_file_is_named_as_the_problem()
    {
        foreach (var empty in new[] { null, string.Empty, "   " })
        {
            Assert.Contains("watched folder is not set", HandoffPaths.RelativeMediaPathForHandoff(empty, "/a/b.mkv").Problem, StringComparison.Ordinal);
        }

        Assert.Contains("did not name a file path", HandoffPaths.RelativeMediaPathForHandoff("/srv/handoff", "  ").Problem, StringComparison.Ordinal);
        // Only the file's own name is named, never the absolute path either side gave: this message can reach an
        // unauthenticated caller, and it must not disclose the layout of Weir's own disk.
        Assert.Equal(
            "The hand-off names 'y.mkv', which is not inside Weir's watched folder for this media type. Point the media manager and Weir at the same folder — both hosts have to see it at that path.",
            HandoffPaths.RelativeMediaPathForHandoff("/srv", "/x/y.mkv").Problem);
        Assert.DoesNotContain("/srv", HandoffPaths.RelativeMediaPathForHandoff("/srv", "/x/y.mkv").Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_no_workflow_watches_is_refused_by_name_with_every_workflow_listed()
    {
        var detail = NoWorkflowWatches.Detail(
            [
                new WorkflowFolder("Movies", "movie", @"C:\Downloads\Completed\Movies"),
                new WorkflowFolder("Kids", "movie", "/media/kids"),
                new WorkflowFolder("TV", "tv", "  "),
            ],
            @"C:\Downloads\Completed\Other\Nosferatu 1922 [PublicHD]\Nosferatu.mkv");

        Assert.Equal(
            "No Weir workflow watches the folder that 'Nosferatu.mkv' is in. " +
            @"Weir's workflows: Movies watches 'C:\Downloads\Completed\Movies'; Kids (Movies) watches '/media/kids'; TV (TV episodes) has no watched folder yet. " +
            "Set a workflow's watched folder (Setup › Workflows) to the folder the download client finishes into, or add a path mapping in the media manager.",
            detail);
        Assert.DoesNotContain("Other", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_with_no_workflows_says_so_and_still_names_only_the_file()
    {
        var detail = NoWorkflowWatches.Detail([], "/x/y/film.mkv");

        Assert.StartsWith("No Weir workflow watches the folder that 'film.mkv' is in. Weir has no workflows yet. Add one in Setup › Workflows", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/x/y", detail, StringComparison.Ordinal);
    }

    // --- import events -------------------------------------------------------------------------

    [Fact]
    public void Dialects_unwrap_each_managers_phrasing()
    {
        Assert.Null(ImportEvents.DialectForSource("sonarr")!.Normalize(Dict("""{"eventType":"Grab","episodes":[{"id":1}]}""")));
        var sonarr = ImportEvents.DialectForSource(" SONARR ")!.Normalize(Dict(
            """{"eventType":"Download","series":{"title":"Test Show"},"episodes":[{"id":9,"seasonNumber":1,"episodeNumber":2.0,"title":"Hello"}],"episodeFile":{"path":"/media/t/x.mkv"}}"""))!;
        Assert.Equal(("imported", "tv", "/media/t/x.mkv", "Test Show", 2), (sonarr.EventKind, sonarr.MediaScope, sonarr.FilePath, sonarr.ShowTitle, (int)sonarr.EpisodeNumber!.Value));

        var deluno = ImportEvents.DialectForSource("deluno")!.Normalize(Dict(
            """{"eventType":"deluno.processor-handoff","handoffId":"h1","libraryId":"lib-1","mediaType":"movies","sourcePath":" /w/f.mkv ","releaseName":"R","callbackPath":"/cb"}"""))!;
        Assert.Equal(("handoff", "movie", "/w/f.mkv", "h1", "lib-1", "/cb", "R"), (deluno.EventKind, deluno.MediaScope, deluno.FilePath, deluno.HandoffId, deluno.LibraryId, deluno.CallbackPath, deluno.ReleaseName));
        Assert.Null(ImportEvents.DialectForSource("deluno")!.Normalize(Dict("""{"eventType":"deluno.processor-handoff","mediaType":"music","sourcePath":"/x"}""")));

        var native = ImportEvents.DialectForSource("native")!.Normalize(Dict("""{"event":"HANDOFF","media_scope":"series","file_path":"/x.mkv","handoff_id":"n1","seasonNumber":0,"season_number":3}"""))!;
        Assert.Equal(("handoff", "tv", "n1", 3), (native.EventKind, native.MediaScope, native.HandoffId, (int)native.SeasonNumber!.Value));
        Assert.Null(ImportEvents.DialectForSource("native")!.Normalize(Dict("""{"event":"imported","title":"no path"}""")));
        Assert.Null(ImportEvents.DialectForSource("native")!.Normalize(Dict("""{"event":"deleted","filePath":"/x","mediaScope":"movie"}""")));
        Assert.Null(ImportEvents.DialectForSource("plex"));
        Assert.Equal(["deluno", "native", "radarr", "sonarr"], ImportEvents.KnownSourceKeys());
    }

    // --- manager dialects ----------------------------------------------------------------------

    [Fact]
    public void The_label_names_the_connection_not_just_the_vendor()
    {
        Assert.Equal("Deluno (Main)", MediaManagerKinds.LabelForConnection("deluno", "Main"));
        Assert.Equal("Radarr (4K)", MediaManagerKinds.LabelForConnection("radarr", "4K"));
        Assert.Equal("Deluno", MediaManagerKinds.LabelForConnection("deluno", "Deluno"));
        Assert.Equal("Media manager (Home)", MediaManagerKinds.LabelForConnection("native", "Home"));
    }

    [Fact]
    public void Scope_capabilities_are_static_so_binding_costs_no_requests()
    {
        Assert.Equal(["deluno", "native", "radarr"], ManagerKindProfiles.KindsServingScope("movie"));
        Assert.Equal(["deluno", "native", "sonarr"], ManagerKindProfiles.KindsServingScope("tv"));
        var deluno = ManagerKindProfiles.CapabilitiesForKind("deluno")!;
        Assert.Equal(["movie", "tv"], deluno.Scopes);
        Assert.True(deluno.ReportsQueue);
        Assert.False(deluno.ReportsLibraryTruth);
        Assert.False(deluno.RemovesQueueItems);
        Assert.True(ManagerKindProfiles.CapabilitiesForKind("sonarr")!.RemovesQueueItems);
    }

    [Theory]
    [InlineData("running", "downloading")]
    [InlineData("queued", "downloading")]
    [InlineData("importing", "importpending")]
    [InlineData("Import Pending", "importpending")]
    [InlineData("failed", "failed")]
    [InlineData("something Weir has never heard of", "downloading")]
    public void An_unrecognised_deluno_state_reads_as_still_in_progress(string reported, string expected) =>
        Assert.Equal(expected, ManagerDialectRules.ExternalQueueStatus(Dict($$"""{"status":"{{reported}}"}""")));

    [Fact]
    public void The_deluno_queue_covers_jobs_and_recent_dispatches_and_drops_rows_without_a_scope()
    {
        var rows = ManagerDialectRules.ExternalQueueEntries(WireJsonParser.Parse(
            """{"jobs":[{"mediaType":"movie","status":"downloading","targetPath":"/media/movies/Solaris.mkv","title":"Solaris","year":1972},{"status":"running","path":"/x.mkv"}],"dispatches":[{"mediaType":"series","state":"completed","path":"/tv/Show/S01E01.mkv","id":7}]}"""))
            .Select(ManagerDialectRules.ExternalQueueRow).OfType<ManagerQueueRow>().ToList();
        Assert.Equal(["movie", "tv"], rows.Select(row => row.Scope));
        Assert.Equal(
            """{"status":"downloading","outputPath":"/media/movies/Solaris.mkv","title":"Solaris","media":{"title":"Solaris","year":1972},"entityId":null}""",
            WireJsonWriter.Dumps(rows[0].Payload, WireJsonFormat.Compact));
        Assert.Equal("completed", ((WireString)rows[1].Payload["status"]).Value);
        Assert.Equal("7", rows[1].Payload["entityId"].ToString());
    }

    [Fact]
    public void A_deluno_manifest_narrows_scopes_and_reads_the_confirmed_library_contract()
    {
        var connection = new ManagerConnection("deluno", "Main", "http://manager.local", "k", 1);
        var description = ManagerDialectRules.ExternalDescription(connection, ManagerKindProfiles.CapabilitiesForKind("deluno")!, WireJsonParser.Parse(
            """{"libraries":[{"id":"abc","name":"Films","mediaType":"movie","path":"/media/movies","rootPath":"/media/movies","importWorkflow":"Refine-Before-Import","processorOutputPath":"/data/refined"},{"mediaType":"movie","path":"/nokey"}],"capabilities":[" Handoff-Status ",5,""]}"""));
        Assert.Equal(["movie"], description.Capabilities.Scopes);
        Assert.Equal(["/media/movies", "/nokey"], description.LibraryRoots);
        var library = Assert.Single(description.Libraries);
        Assert.Equal(new ManagerLibraryDescriptor("abc", "Films", "movie", "/media/movies", "/data/refined", true), library);
        Assert.Equal(["handoff-status"], description.AdvertisedCapabilities!);
    }

    [Fact]
    public void Arr_answers_read_records_roots_and_nested_files()
    {
        Assert.Equal(["movie"], ManagerDialectRules.ArrQueueRows(WireJsonParser.Parse("""{"records":[{"status":"downloading"},"junk"]}"""), "movie").Select(r => r.Scope));
        Assert.Equal(["/media/Solaris/f.mkv"], ManagerDialectRules.ArrLibraryFilePaths(WireJsonParser.Parse("""[{"movieFile":{"path":"/media/Solaris/f.mkv"}},{"movieFile":null},{}]"""), "movieFile"));
        Assert.Equal(["/tv/Show/S01/e.mkv"], ManagerDialectRules.ArrLibraryFilePaths(WireJsonParser.Parse("""[{"path":"/tv/Show/S01/e.mkv"}]"""), null));
        // #544 item 4: a root folder with a null path is skipped, not reported as a root literally called "None".
        // A default for a missing key does not cover a key that is present and null.
        var (roots, libraries) = ManagerDialectRules.ArrRootFolders(WireJsonParser.Parse("""[{"id":3,"path":"/m"},{"id":0,"path":"/z"},{"path":null}]"""), "movie");
        Assert.Equal(["/m", "/z"], roots);
        Assert.Equal(["3", "/z"], libraries.Select(l => l.Key));
    }

    [Fact]
    public void Unreachable_sentences_name_the_connection_and_what_to_fix()
    {
        var connection = new ManagerConnection("radarr", "4K", "http://m", "k");
        Assert.Contains("Radarr (4K)", ManagerDialectRules.Unreachable(connection, new MediaManagerUnreachableException("refused"), "what it is importing"), StringComparison.Ordinal);
        Assert.Contains("refused Weir's API key", ManagerDialectRules.Unreachable(connection, new MediaManagerHttpException("HTTP 401: nope"), "x"), StringComparison.Ordinal);
        var limited = ManagerDialectRules.Unreachable(new ManagerConnection("deluno", "Main", "http://m", "k"), new MediaManagerRateLimitedException("HTTP 429", 30.9), "what it is importing");
        Assert.Contains("rate limiting Weir", limited, StringComparison.Ordinal);
        Assert.Contains("about 30s", limited, StringComparison.Ordinal);
        Assert.Contains("backed off rather than retrying straight away", limited, StringComparison.Ordinal);
        Assert.Equal(
            "Radarr (4K) answered with a redirect to another address. Use the address it redirects to.",
            ManagerDialectRules.Unreachable(connection, new MediaManagerRedirectedException(), "x"));
    }

    [Fact]
    public void A_manager_that_cannot_be_reached_is_told_with_the_address_and_no_transport_text()
    {
        var connection = new ManagerConnection("deluno", "Main", "http://127.0.0.1:7879", "k");
        const string expected = "Weir could not reach Deluno (Main) at http://127.0.0.1:7879. " +
                                "Check the address is right, and that the app is running and reachable from this machine.";

        Assert.Equal(expected, ManagerDialectRules.Unreachable(connection, new MediaManagerUnreachableException("The connection failed: couldn't connect."), "what it manages"));
        Assert.Equal(expected, ManagerDialectRules.Unreachable(connection, new InvalidOperationException("<urlopen error couldn't connect>"), "what it manages"));
        Assert.Equal(
            expected,
            ManagerDialectRules.Unreachable(connection, new MediaManagerHttpException("Only http and https addresses are allowed.", new WireValueException("bad address")), "what it manages"));
    }

    [Fact]
    public void A_download_client_that_cannot_be_reached_is_told_with_the_same_sentence()
    {
        var client = new DownloadClientConnection("transmission", "Main", "http://127.0.0.1:9091", null, null, null);

        Assert.Equal(
            $"Weir could not reach {client.Label} at http://127.0.0.1:9091. Check the address is right, and that the app is running and reachable from this machine.",
            DownloadClientDialectRules.Unreachable(client));
    }

    [Theory]
    [InlineData("HTTP 500")]
    [InlineData("unexpected HTTP 204")]
    [InlineData("HTTP 200: the response was not valid JSON. This does not look like a Sonarr, Radarr or Deluno API at this address.")]
    public void A_manager_that_answers_oddly_is_told_without_the_status_or_the_raw_detail(string detail)
    {
        var connection = new ManagerConnection("radarr", "4K", "http://m", "k");

        var sentence = ManagerDialectRules.Unreachable(connection, new MediaManagerHttpException(detail), "what it is importing");

        Assert.StartsWith("Radarr (4K) did not give Weir the answer it expected when asked what it is importing.", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Filtering_rows_to_the_asked_scope_leaves_a_silent_manager_silent()
    {
        var connection = new ManagerConnection("deluno", "Main", "http://m", "k");
        var reported = new ManagerQueueSignal(connection, SignalStatus.Reported, [new("movie", new WireObject().Set("title", "a film")), new("tv", new WireObject().Set("title", "an episode"))]);
        Assert.Equal(["movie"], ManagerDialectRules.OnlyRowsForScope(reported, "movie").Rows.Select(r => r.Scope));
        var silent = new ManagerQueueSignal(connection, SignalStatus.Unreachable, [], "down");
        Assert.Same(silent, ManagerDialectRules.OnlyRowsForScope(silent, "movie"));
    }

    // --- completion callback -------------------------------------------------------------------

    private static readonly HandoffOrigin Origin = new("deluno", "handoff-1", "/api/integrations/processors/events", "Blade.Runner.2049");
    private static readonly HandoffOrigin DelunoOrigin = new("deluno", "handoff-1", "/api/integrations/processors/events", null, "lib-movies");

    [Fact]
    public void A_written_output_is_reported_as_completed_with_its_path()
    {
        var body = CompletionReports.BuildCompletionBody(Origin, Dict("""{"ok":true,"outcome":"live_output_written","output_file":"D:\\Refined\\Blade.Runner.2049\\film.mkv","removed_audio":["fre","deu"],"removed_subtitles":["spa"]}"""));
        Assert.Equal(
            """{"handoffId":"handoff-1","status":"completed","processorName":"Weir","releaseName":"Blade.Runner.2049","outputPath":"D:\\Refined\\Blade.Runner.2049\\film.mkv","message":"Removed 2 audio tracks and 1 subtitle track.","outputFiles":["D:\\Refined\\Blade.Runner.2049\\film.mkv"]}""",
            WireJsonWriter.Dumps(body, WireJsonFormat.Compact));
    }

    [Fact]
    public void Success_wording_covers_no_remux_pass_through_and_pass_through_after_failure()
    {
        Assert.Contains("No remux was needed", ((WireString)CompletionReports.BuildCompletionBody(Origin, Dict("""{"ok":true,"outcome":"live_skipped_not_required","output_file":"/out/film.mkv"}"""))["message"]).Value, StringComparison.Ordinal);
        Assert.Contains("passed this file through unchanged", ((WireString)CompletionReports.BuildCompletionBody(Origin, Dict("""{"ok":true,"outcome":"live_skipped_not_required","pass_through_unchanged":true}"""))["message"]).Value, StringComparison.Ordinal);
        Assert.StartsWith("Weir could not process this file, so it handed the original back", ((WireString)CompletionReports.BuildCompletionBody(DelunoOrigin, Dict("""{"ok":true,"outcome":"live_output_written","passed_through_after_failure":true}"""))["message"]).Value, StringComparison.Ordinal);
        Assert.Equal("/data/refined/film.mkv", ((WireString)CompletionReports.BuildCompletionBody(DelunoOrigin, Dict("""{"ok":true,"outcome":"live_output_written","output_file":"/local/out/film.mkv"}"""), "/data/refined/film.mkv")["outputPath"]).Value);
    }

    [Fact]
    public void A_download_that_is_no_longer_there_is_reported_as_source_gone_with_no_disposition()
    {
        var body = CompletionReports.BuildCompletionBody(DelunoOrigin, Dict("""{"ok":true,"outcome":"source_gone","reason":"This file is no longer in the watched folder, so there is nothing to do.","relative_media_path":"Film/film.mkv"}"""));

        Assert.Equal(
            """{"handoffId":"handoff-1","status":"failed","processorName":"Weir","libraryId":"lib-movies","message":"The download is no longer there, so Weir had nothing to do.","sourceRemoved":false,"failureClass":"source_gone","outputFiles":[]}""",
            WireJsonWriter.Dumps(body, WireJsonFormat.Compact));
        Assert.False(body.ContainsKey("disposition"));
        Assert.Equal(CompletionReports.SourceGoneMessage, CompletionReports.MessageFor(Dict("""{"ok":true,"outcome":"source_gone"}""")));
        Assert.False(CompletionReports.IsSucceeded(Dict("""{"ok":true,"outcome":"source_gone"}""")));
    }

    [Theory]
    [InlineData(409, """{"message":"Deluno stopped waiting for Film.mkv, which ended dismissed, so this report is too late. No import was queued.","handoffId":"h1","status":"dismissed"}""", true)]
    [InlineData(409, """{"handoffId":"h1","status":"completed"}""", false)]
    [InlineData(409, """{"message":"something else"}""", false)]
    [InlineData(409, """["dismissed"]""", false)]
    [InlineData(404, """{"handoffId":"h1","status":"dismissed"}""", false)]
    [InlineData(202, """{"handoffId":"h1","status":"dismissed"}""", false)]
    public void A_manager_that_stopped_waiting_says_so_with_a_409_naming_the_dismissed_status(int httpStatus, string answer, bool expected) =>
        Assert.Equal(expected, CompletionReports.IsTooLate(httpStatus, WireJsonParser.Parse(answer)));

    [Fact]
    public void An_answer_that_cannot_be_read_is_not_a_manager_that_stopped_waiting() =>
        Assert.False(CompletionReports.IsTooLate(409, null));

    [Fact]
    public void Failures_carry_the_reason_and_what_happened_to_the_source()
    {
        var failed = CompletionReports.BuildCompletionBody(Origin, Dict("""{"ok":false,"outcome":"failed_before_execution","reason":"relative_media_path is required"}"""));
        Assert.Equal("relative_media_path is required", ((WireString)failed["message"]).Value);
        Assert.False(failed.ContainsKey("outputPath"));

        var guardrail = CompletionReports.BuildCompletionBody(Origin, Dict("""{"ok":true,"outcome":"skipped_guardrail","source_folder_skip_reason":"File is too small."}"""));
        Assert.Equal(("failed", "File is too small."), (((WireString)guardrail["status"]).Value, ((WireString)guardrail["message"]).Value));

        Assert.Equal(
            """{"handoffId":"handoff-1","status":"failed","processorName":"Weir","libraryId":"lib-movies","message":"ffmpeg failed","disposition":"held","sourceRemoved":false,"failureClass":"execution","outputFiles":[]}""",
            WireJsonWriter.Dumps(CompletionReports.BuildCompletionBody(DelunoOrigin, Dict("""{"ok":false,"outcome":"failed_execution","reason":"ffmpeg failed","failure_class":"execution"}""")), WireJsonFormat.Compact));

        var deleted = CompletionReports.BuildCompletionBody(DelunoOrigin, Dict("""{"ok":false,"outcome":"skipped_rejected","reason":"No wanted audio language.","rejected_cleanup_status":"deleted"}"""));
        Assert.True(((WireBool)deleted["sourceRemoved"]).Value);
        Assert.False(deleted.ContainsKey("disposition"));

        var rejected = CompletionReports.BuildCompletionBody(DelunoOrigin, Dict("""{"ok":false,"outcome":"failed","reason":"No usable audio.","failure_class":"preflight"}"""), rejected: true);
        Assert.Equal(("rejected", true, "preflight"), (((WireString)rejected["disposition"]).Value, ((WireBool)rejected["sourceRemoved"]).Value, ((WireString)rejected["failureClass"]).Value));
        Assert.False(CompletionReports.BuildCompletionBody(DelunoOrigin, Dict("""{"ok":true,"outcome":"live_output_written"}""")).ContainsKey("disposition"));
    }

    [Fact]
    public void The_tools_own_text_never_reaches_the_report_to_the_manager()
    {
        var body = CompletionReports.BuildCompletionBody(
            DelunoOrigin,
            Dict("""{"ok":false,"outcome":"failed_before_execution","reason":"Weir can't read this file yet.","technical_detail":"[matroska,webm @ 000001b280590a00] EBML header parsing failed C:\\Media\\film.mkv","inspected_source_path":"C:\\Media\\film.mkv"}"""));

        var text = WireJsonWriter.Dumps(body, WireJsonFormat.Compact);
        Assert.Equal("Weir can't read this file yet.", ((WireString)body["message"]).Value);
        Assert.DoesNotContain("technical_detail", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EBML", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\\\Media", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_origin_is_read_from_the_job_payload()
    {
        Assert.Null(HandoffOrigin.FromPayload(Dict("""{"relative_media_path":"a/b.mkv"}""")));
        Assert.Null(HandoffOrigin.FromPayload(null));
        Assert.Null(HandoffOrigin.FromPayload(Dict("""{"origin":{}}""")));
        Assert.Equal(new HandoffOrigin("deluno", "h1", "/cb", "R"), HandoffOrigin.FromPayload(Dict("""{"origin":{"source_key":"deluno","handoff_id":"h1","callback_path":"/cb","release_name":"R"}}""")));
        Assert.Equal("lib-movies", HandoffOrigin.FromPayload(Dict("""{"origin":{"source_key":"deluno","library_id":"lib-movies"}}"""))!.LibraryId);
        Assert.Equal("5", HandoffOrigin.FromPayload(Dict("""{"origin":{"source_key":" deluno ","handoff_id":5,"callback_path":""}}"""))!.HandoffId);
    }

    [Theory]
    [InlineData("/data/refined", "/data/refined/Blade.Runner.2049/film.mkv")]
    [InlineData(@"E:\Deluno\Refined", @"E:\Deluno\Refined\Blade.Runner.2049\film.mkv")]
    [InlineData("E:/Deluno/Refined/", "E:/Deluno/Refined/Blade.Runner.2049/film.mkv")]
    [InlineData(@"\\nas\share\out", @"\\nas\share\out\Blade.Runner.2049\film.mkv")]
    public void Output_paths_are_rebuilt_in_the_managers_own_style(string folder, string expected)
    {
        var joined = CompletionReports.ManagerPathJoin(folder, ["Blade.Runner.2049", "film.mkv"]);
        // "E:/..." has a drive letter, so it is a Windows path and is written with backslashes.
        Assert.Equal(folder.StartsWith("E:/", StringComparison.Ordinal) ? expected.Replace('/', '\\') : expected, joined);
    }

    // --- hand-off ledger -----------------------------------------------------------------------

    [Theory]
    [InlineData("processed", "completed")]
    [InlineData("passed_through", "passed-through")]
    [InlineData("rejected", "rejected")]
    [InlineData("processing_failed", "failed")]
    [InlineData("out_of_schedule", "scheduled")]
    [InlineData("processing", "working")]
    [InlineData("skipped", "failed")]
    [InlineData("blocked_upstream", "queued")]
    [InlineData("unprocessed", "queued")]
    public void File_states_map_to_the_agreed_vocabulary(string status, string state) =>
        Assert.Equal(state, HandoffLedgerRules.FileState(status, null).State);

    [Fact]
    public void A_file_on_hold_is_queued_because_a_file_that_failed_is_never_held()
    {
        Assert.Equal("queued", HandoffLedgerRules.FileState("on_hold", null).State);
    }

    [Fact]
    public void A_failure_with_a_retry_owed_is_scheduled_even_after_the_backoff_ends()
    {
        var future = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(("scheduled", future), HandoffLedgerRules.FileState("processing_failed", future));
        var past = future.AddHours(-3);
        Assert.Equal(("scheduled", past), HandoffLedgerRules.FileState("processing_failed", past));
        Assert.Equal(("failed", (DateTimeOffset?)null), HandoffLedgerRules.FileState("processing_failed", null));
    }

    [Fact]
    public void A_hand_off_is_as_far_along_as_its_least_finished_file()
    {
        Assert.Equal("working", HandoffLedgerRules.Combine(["completed", "queued", "working"]));
        Assert.Equal("scheduled", HandoffLedgerRules.Combine(["completed", "scheduled"]));
        Assert.Equal("failed", HandoffLedgerRules.Combine(["passed-through", "failed", "rejected"]));
        Assert.Equal("passed-through", HandoffLedgerRules.Combine(["completed", "passed-through"]));
        Assert.Equal("completed", HandoffLedgerRules.Combine(["completed"]));
    }

    [Fact]
    public void A_file_the_workflows_rules_left_alone_fails_its_hand_off_only_when_nothing_else_was_delivered()
    {
        Assert.Equal("completed", HandoffLedgerRules.Combine(["completed", "skipped"]));
        Assert.Equal("passed-through", HandoffLedgerRules.Combine(["passed-through", "skipped"]));
        Assert.Equal("failed", HandoffLedgerRules.Combine(["skipped"]));
        Assert.Equal("failed", HandoffLedgerRules.Combine(["skipped", "cancelled"]));
        Assert.Equal("failed", HandoffLedgerRules.Combine(["completed", "skipped", "failed"]));
        Assert.Equal("working", HandoffLedgerRules.Combine(["completed", "skipped", "working"]));
    }

    private const long Mb = 1024 * 1024;

    private static readonly HandoffOrigin FilmOrigin = new("deluno", "h1", "/cb", "Film.2020", "lib-1");

    private static HandoffTarget Film(long? size = 80 * Mb) =>
        new("Film.2020/film.mkv", "completed", "/out/Film.2020/film.mkv", "Remux finished.", SourceSize: size);

    private static HandoffTarget Gallery(long? size = 15 * Mb) =>
        new("Film.2020/Gallery.mkv", "skipped", null, "Skipped because this file is 15.6 MB, under the 50 MB minimum.", SourceSize: size);

    [Fact]
    public void A_folder_report_names_the_files_left_alone_without_failing_when_they_are_smaller_than_what_was_delivered()
    {
        var targets = new[] { Film(), Gallery() };

        var body = FolderHandoffReports.BuildBody(FilmOrigin, targets, "/out/Film.2020", ["/out/Film.2020/film.mkv"]);

        Assert.Equal("completed", ((WireString)body["status"]).Value);
        Assert.Equal("/out/Film.2020", ((WireString)body["outputPath"]).Value);
        Assert.Equal(
            "Weir finished 1 file, ready to import from the output folder. " +
            "Weir left 1 file alone under this workflow's rules. Gallery.mkv: Skipped because this file is 15.6 MB, under the 50 MB minimum.",
            ((WireString)body["message"]).Value);
        Assert.Equal("completed", FolderHandoffReports.State(targets));
    }

    [Fact]
    public void A_folder_report_with_nothing_delivered_and_a_file_left_alone_is_failed_and_says_why()
    {
        var body = FolderHandoffReports.BuildBody(FilmOrigin, [Gallery()], null, []);

        Assert.Equal("failed", ((WireString)body["status"]).Value);
        Assert.Equal("held", ((WireString)body["disposition"]).Value);
        Assert.Equal(
            "Weir finished 0 of 1 file. Weir left 1 file alone under this workflow's rules. " +
            "Gallery.mkv: Skipped because this file is 15.6 MB, under the 50 MB minimum.",
            ((WireString)body["message"]).Value);
        Assert.Equal("failed", FolderHandoffReports.State([Gallery()]));
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(80, false)]
    [InlineData(90, false)]
    public void A_skipped_file_is_only_an_extra_when_it_is_smaller_than_every_delivered_file(long skippedMb, bool extra)
    {
        // Nothing says which file is the film: a skipped file as big as what was delivered might be it.
        var skipped = Gallery(skippedMb * Mb);
        var targets = new[] { Film(), skipped };

        Assert.Equal(extra, FolderHandoffReports.MayBeLeftAlone(skipped, targets));
        Assert.Equal(extra ? "completed" : "failed", FolderHandoffReports.State(targets));
        Assert.Equal(extra ? "completed" : "failed", ((WireString)FolderHandoffReports.BuildBody(FilmOrigin, targets, "/out", [])["status"]).Value);
    }

    [Fact]
    public void A_skipped_file_is_never_an_extra_when_either_size_is_not_known()
    {
        Assert.Equal("failed", FolderHandoffReports.State([Film(), Gallery(null)]));
        Assert.Equal("failed", FolderHandoffReports.State([Film(null), Gallery()]));
    }

    [Fact]
    public void A_skipped_file_beside_a_smaller_delivered_featurette_fails_the_hand_off_and_says_why()
    {
        var film = new HandoffTarget("Film.2020/film.mkv", "skipped", null, "Skipped because this file is 90.0 MB, under the 95 MB minimum.", SourceSize: 90 * Mb);
        var featurette = new HandoffTarget("Film.2020/featurette.mkv", "completed", "/out/Film.2020/featurette.mkv", "Remux finished.", SourceSize: 80 * Mb);

        var body = FolderHandoffReports.BuildBody(FilmOrigin, [film, featurette], null, ["/out/Film.2020/featurette.mkv"]);

        Assert.Equal("failed", ((WireString)body["status"]).Value);
        Assert.Equal("failed", FolderHandoffReports.State([film, featurette]));
        Assert.Equal(
            "Weir finished 1 of 2 files. Weir left 1 file alone under this workflow's rules. " +
            "film.mkv: Skipped because this file is 90.0 MB, under the 95 MB minimum. " +
            "Weir did not take that file for an extra, because it is not smaller than every file it delivered.",
            ((WireString)body["message"]).Value);
    }

    [Fact]
    public void A_failed_file_is_reported_as_one_Weir_could_not_process()
    {
        var failed = new HandoffTarget("Film.2020/Gallery.mkv", "failed", null, "ffmpeg died.", SourceSize: 15 * Mb);

        var body = FolderHandoffReports.BuildBody(FilmOrigin, [Film(), failed], null, ["/out/Film.2020/film.mkv"]);

        Assert.Equal("Weir finished 1 of 2 files. It could not process 1 file: Gallery.mkv: ffmpeg died.", ((WireString)body["message"]).Value);
    }

    [Fact]
    public void A_pass_a_guardrail_skipped_is_a_file_left_alone_not_a_failure()
    {
        var skipped = Dict("""{"ok": true, "outcome": "skipped_guardrail", "reason": "Skipped because this file is 15.6 MB, under the 50 MB minimum."}""");
        var failed = Dict("""{"ok": false, "outcome": "failed_execution", "reason": "ffmpeg died"}""");

        Assert.Equal("skipped", FolderHandoffReports.TargetResult(skipped));
        Assert.Equal("failed", FolderHandoffReports.TargetResult(failed));
    }

    [Fact]
    public void A_cancelled_file_ends_its_hand_off_only_when_nothing_else_was_delivered()
    {
        Assert.Equal(("cancelled", (DateTimeOffset?)null), HandoffLedgerRules.FileState("cancelled", null));
        Assert.Equal("cancelled", HandoffLedgerRules.Combine(["cancelled"]));
        Assert.Equal("cancelled", HandoffLedgerRules.Combine(["cancelled", "cancelled"]));
        // A pack with the rest cleaned is completed, so the manager still imports what Weir wrote.
        Assert.Equal("completed", HandoffLedgerRules.Combine(["completed", "cancelled"]));
        Assert.Equal("queued", HandoffLedgerRules.Combine(["cancelled", "queued"]));
        Assert.Equal("failed", HandoffLedgerRules.Combine(["cancelled", "failed"]));
        Assert.Equal("passed-through", HandoffLedgerRules.Combine(["cancelled", "passed-through"]));
    }

    [Fact]
    public void Status_json_writes_utc_with_a_z_and_drops_zero_microseconds()
    {
        var status = new HandoffStatus("h1", "queued", new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero), 1, null, null, null);
        Assert.Equal(
            """{"handoffId":"h1","state":"queued","queuePosition":1,"scheduledFor":null,"lastChangedUtc":"2026-09-17T10:00:00Z","outputPath":null,"outputFiles":null,"message":null}""",
            WireJsonWriter.Dumps(status.AsJson(), WireJsonFormat.Response));
        Assert.Equal("2026-09-17T10:00:00.000500Z", ((WireString)(status with { LastChangedAt = status.LastChangedAt.AddTicks(5000) }).AsJson()["lastChangedUtc"]).Value);
    }

    // --- intake --------------------------------------------------------------------------------

    private static MediaManagerImportEvent Handoff(string path, string? handoffId = "handoff-1", string scope = "movie") => new()
    {
        SourceKey = "deluno",
        EventKind = "handoff",
        MediaScope = scope,
        FilePath = path,
        HandoffId = handoffId,
        CallbackPath = handoffId is null ? null : "/api/integrations/processors/events",
        ReleaseName = "Blade.Runner.2049",
        LibraryId = "lib-1",
    };

    [Fact]
    public void The_job_payload_is_written_byte_for_byte()
    {
        var importEvent = Handoff("/w/Blade.Runner.2049/film.mkv");
        var payload = IntakeRules.Payload(importEvent, new IntakeLibrary(4, "movie", "/w"), "Blade.Runner.2049", "Blade.Runner.2049/film é.mkv");
        Assert.Equal(
            """{"relative_media_path":"Blade.Runner.2049/film \u00e9.mkv","media_scope":"movie","trigger":"webhook","library_id":4,"origin":{"source_key":"deluno","handoff_id":"handoff-1","callback_path":"/api/integrations/processors/events","release_name":"Blade.Runner.2049","library_id":"lib-1"}}""",
            IntakeRules.PayloadJson(payload));
        Assert.Equal(
            """{"relative_media_path":"a.mkv","media_scope":"tv","trigger":"webhook"}""",
            IntakeRules.PayloadJson(IntakeRules.Payload(Handoff("/x/a.mkv", null, "tv") with { CallbackPath = null }, null, "a.mkv", "a.mkv")));
    }

    [Fact]
    public void Dedupe_keys_keep_their_stored_shape()
    {
        var baseKey = IntakeRules.BaseDedupeKey(Handoff("/w/x.mkv"), () => Guid.Empty);
        Assert.Equal("processing.file.remux_pass.v1:deluno:handoff:handoff-1", baseKey);
        Assert.Equal("processing.file.remux_pass.v1:00000000000000000000000000000000", IntakeRules.BaseDedupeKey(Handoff("/w/x.mkv", null), () => Guid.Empty));
        Assert.Equal(baseKey, IntakeRules.DedupeKeyFor(baseKey, ["Film/film.mkv"], "Film/film.mkv", "Film/film.mkv"));
        Assert.Equal(baseKey + ":Film/film.mkv", IntakeRules.DedupeKeyFor(baseKey, ["Film/film.mkv"], "Film/film.mkv", "Film"));
    }

    [Fact]
    public void The_library_is_chosen_by_the_deepest_watched_folder_preferring_the_hand_offs_media_type()
    {
        IntakeLibrary[] libraries = [new(1, "movie", "/media"), new(2, "movie", "/media/films"), new(3, "tv", "/media/films/4k"), new(4, "movie", "/media/films")];
        Assert.Equal(2, IntakeRules.ChooseLibrary(libraries, Handoff("/media/films/4k/x/film.mkv"))!.Value.Library.Id);
        Assert.Equal(3, IntakeRules.ChooseLibrary(libraries, Handoff("/media/films/4k/x/ep.mkv", scope: "tv"))!.Value.Library.Id);
        Assert.Equal("x/film.mkv", IntakeRules.ChooseLibrary(libraries, Handoff("/media/films/x/film.mkv"))!.Value.Resolved.RelativeMediaPath);
        Assert.Null(IntakeRules.ChooseLibrary(libraries, Handoff("/elsewhere/film.mkv")));
    }

    [Fact]
    public void A_folder_means_its_videos_without_the_samples()
    {
        List<IReadOnlyList<string>> videos = [["Sample", "sample.mkv"], ["Film.mkv"], ["extras", "film-sample.mp4"], ["b.MKV"]];
        Assert.Equal(["b.MKV", "Film.mkv"], IntakeRules.ChooseFolderVideos(videos, windows: true).Select(parts => string.Join('/', parts)));
        Assert.Equal(["Film.mkv", "b.MKV"], IntakeRules.ChooseFolderVideos(videos, windows: false).Select(parts => string.Join('/', parts)));
        List<IReadOnlyList<string>> onlySamples = [["sample.mkv"]];
        Assert.Single(IntakeRules.ChooseFolderVideos(onlySamples, windows: false));
        Assert.True(IntakeRules.IsMediaCandidateName("Film.MKV"));
        Assert.False(IntakeRules.IsMediaCandidateName(".mkv"));
        Assert.False(IntakeRules.IsMediaCandidateName("movie.nfo"));
    }

    // --- library files and file changes (#507) -------------------------------------------------

    [Fact]
    public void Movie_library_files_carry_the_movies_own_id_and_title()
    {
        var files = ManagerDialectRules.ArrMovieLibraryFiles(WireJsonParser.Parse(
            """[{"id":7,"title":"Solaris","movieFile":{"path":"/media/Solaris/f.mkv"}},{"id":8,"title":"No File"},{"id":9,"movieFile":{"path":"/media/9/f.mkv"}}]"""));
        Assert.Equal(
            [new ManagerLibraryFile("7", "Solaris", "/media/Solaris/f.mkv"), new ManagerLibraryFile("9", "9", "/media/9/f.mkv")],
            files);
    }

    [Fact]
    public void Episode_library_files_are_tagged_with_the_series_already_looked_up()
    {
        var files = ManagerDialectRules.ArrEpisodeLibraryFiles(
            WireJsonParser.Parse("""[{"path":"/tv/Show/S01/e01.mkv"},{"path":"/tv/Show/S01/e02.mkv"},{"seasonNumber":1}]"""), "12", "Show");
        Assert.Equal(
            [new ManagerLibraryFile("12", "Show", "/tv/Show/S01/e01.mkv"), new ManagerLibraryFile("12", "Show", "/tv/Show/S01/e02.mkv")],
            files);
    }

    [Theory]
    [InlineData("movie", "RescanMovie", "movieId")]
    [InlineData("tv", "RescanSeries", "seriesId")]
    public void The_rescan_command_matches_each_products_own_class(string scope, string name, string idProperty) =>
        Assert.Equal((name, idProperty), ManagerDialectRules.ArrRescanCommand(scope));

    [Theory]
    [InlineData("/media/Movies/f.mkv", "/media/Movies/f.mkv", true)]
    [InlineData(@"D:\Media\Movies\f.mkv", "d:/media/movies/f.mkv", true)]
    [InlineData("/media/Movies/f.mkv", "/media/Movies/g.mkv", false)]
    [InlineData("", "", false)]
    public void Paths_compare_ignoring_case_and_separator_style(string left, string right, bool equal) =>
        Assert.Equal(equal, LibraryFileChangeRules.PathsEqual(left, right));

    [Fact]
    public void The_warning_names_the_manager_and_says_it_will_catch_up_on_its_own()
    {
        var warning = LibraryFileChangeRules.CouldNotTellWarning(new ManagerConnection("radarr", "4K", "http://m", "k"));
        Assert.Equal("Weir cleaned the file but couldn't tell Radarr (4K); it will catch up at its next disk scan.", warning);
    }
}
