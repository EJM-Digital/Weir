using System.Globalization;
using System.Net;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Activity;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The edges of taking in a hand-off: who is let in, which files a folder or a list means, what happens to a pass already queued for
/// the file, a file a person chose to keep and a source Weir already cleaned. Each case sits where a one-step change to the code
/// would give another answer.
/// </summary>
public sealed class MediaManagerIntakeMutationTests : IDisposable
{
    private const string Callback = "/api/integrations/processors/events";
    private const string Film = "Film/film.mkv";
    private const string Remux = "processing.file.remux_pass.v1";
    private const string PassThrough = "processing.file.pass_through.v1";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PublishedTopics _published;
    private long _libraryId;

    public MediaManagerIntakeMutationTests() => _published = new PublishedTopics(_fixture.Changes);

    public void Dispose()
    {
        _published.Dispose();
        _fixture.Dispose();
    }

    private string Watched => _fixture.Store.Home.Join("movies");

    private async Task SetUpAsync(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Join(Watched, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "film");
        }

        _libraryId = await _fixture.LibraryAsync("movie", Watched);
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _fixture.DelunoListsNoLibraries();
        _fixture.Http.Json(HttpMethod.Post, Callback, "{}", HttpStatusCode.Accepted);
    }

    private MediaManagerImportEvent Handoff(string? handoffId, string file = Film, string sourceKey = "deluno", IReadOnlyList<string>? listed = null) => new()
    {
        SourceKey = sourceKey,
        EventKind = MediaManagerImportEvent.Handoff,
        MediaScope = "movie",
        FilePath = Path.Join(Watched, file),
        HandoffId = handoffId,
        CallbackPath = Callback,
        LibraryId = "lib-1",
        SourceFiles = listed,
    };

    private Task<string> EnqueueAsync(MediaManagerImportEvent importEvent) => _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, importEvent));

    private Task QueuePassAsync(string kind, string key, string payloadExtra, string? notBefore = null, string path = Film)
    {
        var before = notBefore is null ? "NULL" : $"'{notBefore}'";
        return _fixture.Store.Execute(
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, not_before) VALUES " +
            $"('{key}', '{kind}', '{{\"relative_media_path\":\"{path}\",\"media_scope\":\"movie\",\"library_id\":{_libraryId.ToString(CultureInfo.InvariantCulture)}{payloadExtra}}}', 'pending', {before})");
    }

    private async Task<WireObject> PayloadOfAsync(string kind) =>
        (WireObject)WireJsonParser.Parse((await _fixture.Jobs.ListAsync()).Single(job => job.JobKind == kind).PayloadJson!);

    // ---- who is let in ----

    [Fact]
    public async Task A_caller_proved_by_a_connections_own_secret_on_a_hand_off_route_is_authenticated_as_that_connection()
    {
        var id = await _fixture.AddConnectionAsync("radarr");
        var row = (await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, id)))!;
        var secret = await _fixture.Db(uow => _fixture.Connections.RotateWebhookSecretAsync(uow, row));

        var identity = await _fixture.Db(uow => _fixture.Intake.RequireSecretAsync(uow, secret, "radarr"));

        Assert.True(identity.Authenticated);
        Assert.Equal(id, identity.ConnectionId);
    }

    [Fact]
    public async Task A_caller_proved_only_by_the_instance_secret_on_a_hand_off_route_is_authenticated_but_attributed_to_no_connection()
    {
        using var fixture = new MediaManagerFixture(("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", "instance-secret"));
        await fixture.AddConnectionAsync("radarr");

        var identity = await fixture.Db(uow => fixture.Intake.RequireSecretAsync(uow, "instance-secret", "radarr"));

        Assert.True(identity.Authenticated);
        Assert.Null(identity.ConnectionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_caller_that_presents_no_secret_proves_nothing_even_to_a_connection_stored_with_an_empty_secret(string? presented)
    {
        var id = await _fixture.AddConnectionAsync("radarr");
        await _fixture.Store.Execute($"UPDATE media_manager_connections SET webhook_secret_ciphertext = '' WHERE id = {id}");

        var refused = await Assert.ThrowsAsync<IntakeRefusedException>(() => _fixture.Db(uow => _fixture.Intake.RequireSecretAsync(uow, presented, "radarr")));

        Assert.Equal(401, refused.StatusCode);
    }

    // ---- a hand-off that names no file ----

    [Fact]
    public async Task A_hand_off_naming_no_file_is_told_so_when_its_scope_has_a_workflow_and_told_nowhere_is_watched_when_it_has_none()
    {
        await _fixture.LibraryAsync("movie", Watched);
        var nothing = new MediaManagerImportEvent { SourceKey = "deluno", EventKind = MediaManagerImportEvent.Handoff, MediaScope = "movie", FilePath = " ", HandoffId = "h-1" };

        var (library, withWorkflow) = await _fixture.Db(uow => MediaManagerIntake.LibraryForHandoffAsync(uow, nothing));
        await _fixture.Store.Execute("DELETE FROM libraries");
        var (none, withoutWorkflow) = await _fixture.Db(uow => MediaManagerIntake.LibraryForHandoffAsync(uow, nothing));

        Assert.NotNull(library);
        Assert.Equal("The hand-off did not name a file path.", withWorkflow.Problem);
        Assert.Null(none);
        Assert.Equal(
            "Weir's watched folder is not set for this media scope, so there is nowhere to resolve the hand-off against.",
            withoutWorkflow.Problem);
    }

    // ---- which files a folder or a list means ----

    private IntakeLibrary Library() => new(1, "movie", Watched);

    [Fact]
    public void A_folder_hand_off_finds_the_videos_in_the_folders_inside_it()
    {
        Directory.CreateDirectory(Path.Join(Watched, "Pack", "Season 1"));
        File.WriteAllText(Path.Join(Watched, "Pack", "Season 1", "Ep1.mkv"), "x");

        var files = MediaManagerIntake.HandoffMediaFiles(Library(), "Pack");

        Assert.Equal(["Pack/Season 1/Ep1.mkv"], files);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("./")]
    public void A_folder_hand_off_of_the_watched_folder_itself_names_its_videos_without_an_empty_or_dotted_part(string relative)
    {
        Directory.CreateDirectory(Path.Join(Watched, "Pack"));
        File.WriteAllText(Path.Join(Watched, "Pack", "Ep1.mkv"), "x");

        var files = MediaManagerIntake.HandoffMediaFiles(Library(), relative);

        Assert.Equal(["Pack/Ep1.mkv"], files);
    }

    [Fact]
    public void A_hand_off_that_names_one_file_and_lists_it_is_that_file_whatever_its_name()
    {
        Directory.CreateDirectory(Path.Join(Watched, "Pack"));
        File.WriteAllText(Path.Join(Watched, "Pack", "readme.nfo"), "x");
        var importEvent = new MediaManagerImportEvent
        {
            SourceKey = "deluno",
            EventKind = MediaManagerImportEvent.Handoff,
            MediaScope = "movie",
            FilePath = Path.Join(Watched, "Pack", "readme.nfo"),
            SourceFiles = [Path.Join(Watched, "Pack", "readme.nfo")],
        };

        var files = MediaManagerIntake.HandoffListedFiles(Library(), "Pack/readme.nfo", importEvent);

        Assert.Equal(["Pack/readme.nfo"], files);
    }

    [Fact]
    public async Task A_hand_off_that_lists_files_queues_only_those()
    {
        await SetUpAsync("Pack/a.mkv", "Pack/b.mkv");

        await EnqueueAsync(Handoff("h-1", "Pack", listed: [Path.Join(Watched, "Pack", "a.mkv")]));

        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{Remux}'"));
        Assert.Equal("Pack/a.mkv", await _fixture.Store.ScalarText("SELECT relative_path FROM media_manager_handoff_targets"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoff_targets"));
    }

    [Fact]
    public async Task A_hand_off_with_an_empty_list_of_files_means_the_videos_in_the_folder()
    {
        await SetUpAsync("Pack/a.mkv", "Pack/b.mkv");

        await EnqueueAsync(Handoff("h-1", "Pack", listed: []));

        Assert.Equal(2, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{Remux}'"));
    }

    // ---- a pass already queued for the file ----

    private Task QueueDetectedPassAsync() => QueuePassAsync(Remux, "detected-by-scan", ",\"trigger\":\"scan\"");

    [Fact]
    public async Task A_hand_off_for_a_file_with_a_pass_queued_takes_that_pass_over_instead_of_queuing_another()
    {
        await SetUpAsync(Film);
        await QueueDetectedPassAsync();
        await _published.TakeAsync();

        await EnqueueAsync(Handoff("h-1"));

        var job = Assert.Single(await _fixture.Jobs.ListAsync());
        Assert.StartsWith(IntakeRules.RemuxDedupeKey("deluno", "h-1"), job.DedupeKey, StringComparison.Ordinal);
        var payload = (WireObject)WireJsonParser.Parse(job.PayloadJson!);
        Assert.Equal("webhook", WireConvert.Str(payload.Get("trigger")!));
        Assert.Equal("h-1", WireConvert.Str(((WireObject)payload.Get("origin")!).Get("handoff_id")!));
        Assert.Contains(DataTopics.Jobs, await _published.TakeAsync());
    }

    [Fact]
    public async Task A_send_with_no_hand_off_id_takes_the_pass_over_but_leaves_it_under_its_own_key()
    {
        await SetUpAsync(Film);
        await QueueDetectedPassAsync();

        await EnqueueAsync(Handoff(null));

        var job = Assert.Single(await _fixture.Jobs.ListAsync());
        Assert.Equal("detected-by-scan", job.DedupeKey);
        Assert.Equal("webhook", WireConvert.Str((await PayloadOfAsync(Remux)).Get("trigger")!));
    }

    [Fact]
    public async Task A_look_booked_at_a_file_that_is_back_is_started_and_taken_over_under_its_own_key()
    {
        await SetUpAsync(Film);
        await QueuePassAsync(PassThrough, "look-at-film", ",\"gone_looks\":1", notBefore: "2099-01-01 00:00:00");
        await _published.TakeAsync();

        await EnqueueAsync(Handoff("h-1"));

        var look = Assert.Single(await _fixture.Jobs.ListAsync());
        Assert.Equal("look-at-film", look.DedupeKey);
        Assert.Null(look.NotBefore);
        Assert.Equal("h-1", WireConvert.Str(((WireObject)(await PayloadOfAsync(PassThrough)).Get("origin")!).Get("handoff_id")!));
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{Remux}'"));
    }

    [Fact]
    public async Task A_look_booked_for_another_managers_send_is_started_but_left_with_it_and_the_new_send_is_refused()
    {
        await SetUpAsync(Film);
        await QueuePassAsync(
            PassThrough, "look-at-film", ",\"gone_looks\":1,\"origin\":{\"source_key\":\"sonarr\",\"handoff_id\":\"h-1\",\"callback_path\":\"" + Callback + "\"}",
            notBefore: "2099-01-01 00:00:00");
        await _published.TakeAsync();

        await EnqueueAsync(Handoff("h-1"));

        var look = Assert.Single(await _fixture.Jobs.ListAsync());
        Assert.Null(look.NotBefore);
        Assert.Equal("sonarr", WireConvert.Str(((WireObject)(await PayloadOfAsync(PassThrough)).Get("origin")!).Get("source_key")!));
        Assert.Contains(DataTopics.Jobs, await _published.TakeAsync());
        var row = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "h-1")))!;
        var targets = await _fixture.Db(uow => _fixture.Targets.ListAsync(uow, row.Id));
        Assert.Equal([Film], targets.Select(target => target.RelativePath));
    }

    // ---- a file a person chose to keep ----

    private async Task KeepAsync(long sizeOffset = 0, long mtimeOffset = 0)
    {
        var fingerprint = SourceFiles.Fingerprint(Path.Join(Watched, Film));
        await _fixture.Db(async uow =>
        {
            await _fixture.SkipMarkers.SetAsync(uow, _libraryId, Film, fingerprint.SizeBytes + sizeOffset, fingerprint.ModifiedTimeNs + mtimeOffset);
            return true;
        });
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task A_kept_file_that_differs_from_the_kept_one_in_size_or_in_time_alone_is_queued(long sizeOffset, long mtimeOffset)
    {
        await SetUpAsync(Film);
        await KeepAsync(sizeOffset, mtimeOffset);

        await EnqueueAsync(Handoff(null));

        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{Remux}'"));
    }

    [Fact]
    public async Task A_kept_file_a_manager_sends_with_a_hand_off_is_covered_by_it_and_answered_as_failed_for_the_reason_it_is_kept()
    {
        await SetUpAsync(Film);
        await KeepAsync();

        await EnqueueAsync(Handoff("h-kept"));

        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{Remux}'"));
        var row = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "h-kept")))!;
        var targets = await _fixture.Db(uow => _fixture.Targets.ListAsync(uow, row.Id));
        var target = Assert.Single(targets);
        Assert.Equal(Film, target.RelativePath);
        Assert.Equal(HandoffLedgerRules.Failed, target.Result);
        Assert.Equal(HandoffLedgerRules.Failed, row.State);
        Assert.Contains("A person chose to keep this file without processing it again.", row.Message ?? target.Message);
    }

    [Fact]
    public async Task A_kept_file_sent_without_a_hand_off_id_leaves_one_line_in_activity_that_says_who_sent_it_and_that_it_was_left_alone()
    {
        await SetUpAsync(Film);
        await KeepAsync();

        await EnqueueAsync(Handoff(null, sourceKey: "radarr"));

        Assert.Equal("processing", await _fixture.Store.ScalarText("SELECT module FROM activity_events WHERE event_type = 'processing.file_removal_kept'"));
        Assert.Equal(
            $$"""{"relative_media_path":"{{Film}}","manager":"Radarr","trigger":"webhook","result":"skipped"}""",
            await _fixture.Store.ScalarText("SELECT detail FROM activity_events WHERE event_type = 'processing.file_removal_kept'"));
    }

    // ---- a source Weir already cleaned ----

    private async Task CleanedBeforeAsync(string outputFolder)
    {
        var source = Path.Join(Watched, Film);
        var copy = _fixture.Store.Home.Join("out", "film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await File.WriteAllTextAsync(copy, "film");
        var modifiedNs = (File.GetLastWriteTimeUtc(source) - DateTime.UnixEpoch).Ticks * 100;
        await _fixture.Store.Execute(
            "INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at, source_size, source_mtime_ns) VALUES " +
            $"({_libraryId}, '{Film}', '{copy.Replace("'", "''", StringComparison.Ordinal)}', 4, 1, '2026-10-07 04:00:00.000000', 4, {modifiedNs.ToString(CultureInfo.InvariantCulture)})");
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '{outputFolder.Replace("'", "''", StringComparison.Ordinal)}' WHERE id = {_libraryId}");
    }

    [Fact]
    public async Task A_repeat_with_a_callback_but_no_hand_off_id_is_left_alone_without_a_report()
    {
        await SetUpAsync(Film);
        await CleanedBeforeAsync(_fixture.Store.Home.Join("out"));

        await EnqueueAsync(Handoff(null));

        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, Callback));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_skipped_repeat'"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("OUT", true)]
    [InlineData("  OUT  ", true)]
    public async Task A_repeat_is_answered_with_the_output_folder_only_when_the_workflow_names_one(string folder, bool named)
    {
        await SetUpAsync(Film);
        _fixture.Http.Json(
            HttpMethod.Get,
            "/api/integrations/external/manifest",
            """{"name":"Deluno","capabilities":[],"libraries":[{"id":"lib-1","name":"Movies","mediaType":"movies","rootPath":"/movies","importWorkflow":"refine-before-import","processorOutputPath":"D:\\Ready\\Movies"}]}""");
        var output = folder.Replace("OUT", _fixture.Store.Home.Join("out"), StringComparison.Ordinal);
        await CleanedBeforeAsync(output);

        await EnqueueAsync(Handoff("h-2"));

        var sent = WireConvert.Str(((WireObject)_fixture.Http.RequestsTo(HttpMethod.Post, Callback).Single().Json!).Get("outputPath")!);
        Assert.Equal(named ? @"D:\Ready\Movies\film.mkv" : _fixture.Store.Home.Join("out", "film.mkv"), sent);
    }
}
