using System.Globalization;
using System.Net;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// <see cref="HistoryFileRemovalService"/> against a real database and real temporary folders, with each manager
/// behind <see cref="FakeManagerHttp"/> (#785). Covers every route "delete" and "keep" can take — Weir alone,
/// Sonarr/Radarr's queue, and a Deluno-style hand-off — and the file-lifecycle containment the "Weir alone" route
/// depends on.
/// </summary>
public sealed partial class HistoryFileRemovalServiceTests : IDisposable
{
    private const string EventsPath = "/api/integrations/processors/events";
    private const string ManifestPath = "/api/integrations/external/manifest";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FileSkipMarkerStore _skipMarkers = new();

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private HistoryFileRemovalService Service() =>
        new(_fixture.Ports, _fixture.Connections, _fixture.Reporter, new RejectRoutes(_fixture.Ports, _fixture.Reporter), _fixture.Libraries, _fixture.Files, _skipMarkers);

    private async Task<long> LibraryAsync(string rejectedFileAction = "leave")
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        return Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, rejected_file_action, display_order) " +
            "VALUES ('Movies', 'movie', $w, $o, $k, $action, 1) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work),
            ("$action", rejectedFileAction))), CultureInfo.InvariantCulture);
    }

    private Task LinkAsync(long libraryId, long connectionId) =>
        _fixture.Store.Execute($"INSERT INTO library_manager_links (library_id, connection_id) VALUES ({libraryId}, {connectionId})");

    /// <summary>
    /// Seeds a file row exactly as it stands the moment it becomes failed or rejected: with the fingerprint of
    /// <paramref name="source"/> on it when the source exists, matching what <c>RemuxPassFileState</c> now records at
    /// that moment (#786 review of #785). Null <paramref name="source"/> for a test whose file never existed.
    /// </summary>
    private async Task<ProcessingFileRecord> FileRowAsync(long libraryId, string relativePath, string? source, string status = "processing_failed")
    {
        var fingerprint = source is not null && File.Exists(source) ? SourceFiles.Fingerprint(source) : (SourceFingerprint?)null;
        await _fixture.Store.Execute(
            "INSERT INTO files (library_id, relative_path, status, status_reason, fingerprint_size_bytes, fingerprint_mtime_ns) " +
            $"VALUES ({libraryId}, '{relativePath}', '{status}', 'ffmpeg could not read the audio track.', " +
            $"{(fingerprint is { } f ? f.SizeBytes.ToString(CultureInfo.InvariantCulture) : "NULL")}, " +
            $"{(fingerprint is { } f2 ? f2.ModifiedTimeNs.ToString(CultureInfo.InvariantCulture) : "NULL")})");
        return await _fixture.Db(uow => _fixture.Files.FindAsync(uow, libraryId, relativePath)) ?? throw new InvalidOperationException("Seeded row not found.");
    }

    /// <summary>A live hand-off origin for this file: a remux job carrying it, and a ledger row not yet answered.</summary>
    private async Task GiveHandoffOriginAsync(long libraryId, string relativePath, string handoffId)
    {
        await _fixture.Db(async uow => { await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", handoffId, libraryId, relativePath); return 0; });
        var payload = $$$"""{"relative_media_path":"{{{relativePath}}}","library_id":{{{libraryId}}},"origin":{"source_key":"deluno","handoff_id":"{{{handoffId}}}","callback_path":"{{{EventsPath}}}"}}""";
        await _fixture.Jobs.EnqueueOrGetAsync($"seed:{handoffId}", "processing.file.remux_pass.v1", payload);
    }

    // --- who qualifies for the choice ------------------------------------------------------------------

    [Fact]
    public async Task A_finished_title_does_not_qualify_for_a_choice()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source, "processed");

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));

        Assert.False(options.RequiresChoice);
    }

    [Fact]
    public async Task A_failed_title_whose_file_is_gone_does_not_qualify_for_a_choice()
    {
        var library = await LibraryAsync();
        // Deliberately never written to disk.
        var file = await FileRowAsync(library, "Film/film.mkv", null);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));

        Assert.False(options.RequiresChoice);
    }

    // --- Weir alone --------------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_linked_manager_delete_removes_the_file_itself()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        Assert.True(options.RequiresChoice);
        Assert.False(options.DeleteHandledByManager);
        Assert.Null(options.ManagerLabel);
        Assert.Null(options.DeleteRefusedReason);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.False(File.Exists(source));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{Weir.Core.Activity.ActivityEventTypes.ProcessingFileRemovalDeleted}'"));
    }

    [Fact]
    public async Task With_no_linked_manager_keep_records_a_skip_marker_and_forgets_the_row()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        Assert.False(options.KeepNotifiesManager);

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, null, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.True(File.Exists(source));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM file_skip_markers"));
    }

    [Fact]
    public async Task Delete_refuses_a_relative_path_that_escapes_the_watched_folder()
    {
        var library = await LibraryAsync();
        var file = await FileRowAsync(library, "../outside.mkv", null);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    // --- through Sonarr / Radarr's queue ------------------------------------------------------------------

    [Fact]
    public async Task With_radarr_linked_delete_removes_the_queue_item_through_radarr()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        var connection = await _fixture.AddConnectionAsync("radarr", "http://10.0.0.5:7878", "k");
        await LinkAsync(library, connection);
        var outputPath = Path.GetFullPath(source).Replace('\\', '/');
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/queue", $$"""{"records":[{"id":55,"outputPath":"{{outputPath}}","downloadId":"dl1"}]}""");
        _fixture.Http.Route(HttpMethod.Delete, "/api/v3/queue/55", _ => FakeManagerHttp.Response(HttpStatusCode.OK));

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        Assert.True(options.DeleteHandledByManager);
        Assert.Equal("Radarr on 10.0.0.5", options.ManagerLabel);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Delete, "/api/v3/queue/55"));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    /// <summary>
    /// A linked manager whose queue does not actually reference this file (the same "cannot tell which download"
    /// refusal the automatic reject job hits) is not one that "can do it": the wording and the action must agree
    /// (#786 review of #785), so neither names the manager. The workflow is linked, so the download belongs to its client:
    /// Weir offers no delete of its own and does not delete the file.
    /// </summary>
    [Fact]
    public async Task A_linked_manager_with_no_matching_queue_item_leaves_the_file_to_the_download_client()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        var connection = await _fixture.AddConnectionAsync("radarr", "http://10.0.0.5:7878", "k");
        await LinkAsync(library, connection);
        _fixture.Http.Json(HttpMethod.Get, "/api/v3/queue", """{"records":[]}""");

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        Assert.False(options.DeleteHandledByManager);
        Assert.Null(options.ManagerLabel);
        Assert.Contains("This workflow is linked to Radarr", options.DeleteRefusedReason, StringComparison.Ordinal);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.True(File.Exists(source));
        Assert.Contains("This workflow is linked to Radarr, so the original stays with your download client", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Delete, "/api/v3/queue/55"));
    }

    // --- through a hand-off (Deluno) -----------------------------------------------------------------------

    [Fact]
    public async Task With_a_live_handoff_delete_reports_rejected_and_leaves_the_file_for_the_manager_to_remove()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await GiveHandoffOriginAsync(library, "Film/film.mkv", "h1");
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        Assert.True(options.DeleteHandledByManager);
        Assert.Equal("Deluno on 192.0.2.30", options.ManagerLabel);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.True(File.Exists(source), "the download is the manager's and its client's to remove");
        Assert.Contains("Deluno on 192.0.2.30 will remove the download; Weir left it in place.", outcome.Message, StringComparison.Ordinal);
        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var body = (Weir.Core.Json.WireObject)post.Json!;
        Assert.Equal("rejected", Weir.Core.Json.WireConvert.Str(body["disposition"]));
        Assert.False(((Weir.Core.Json.WireBool)body["sourceRemoved"]).Value);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task A_handoff_manager_that_cannot_replace_the_release_leaves_the_file_to_the_download_client()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await GiveHandoffOriginAsync(library, "Film/film.mkv", "h2");
        // No reject-regrab capability advertised: the handoff target cannot take the rejection, but it is still the
        // only linked connection, so there is no queue route to fall back to either.
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":[],"libraries":[]}""");

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        Assert.False(options.DeleteHandledByManager);
        Assert.Contains("This workflow is linked to Deluno", options.DeleteRefusedReason, StringComparison.Ordinal);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.True(File.Exists(source));
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
    }

    [Fact]
    public async Task With_a_live_handoff_keep_tells_the_manager_it_will_not_be_imported()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await GiveHandoffOriginAsync(library, "Film/film.mkv", "h3");
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        Assert.True(options.KeepNotifiesManager);
        Assert.Equal("Deluno on 192.0.2.30", options.ManagerLabel);

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, null, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.True(File.Exists(source));
        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var body = (Weir.Core.Json.WireObject)post.Json!;
        Assert.Equal("failed", Weir.Core.Json.WireConvert.Str(body["status"]));
        // "held", not "rejected": the file is not going anywhere, so nothing is reported removed.
        Assert.Equal("held", Weir.Core.Json.WireConvert.Str(body["disposition"]));
        Assert.False(((Weir.Core.Json.WireBool)body["sourceRemoved"]).Value);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM file_skip_markers"));
    }

    [Fact]
    public async Task A_handoff_manager_that_refuses_the_keep_report_fails_the_whole_action()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await GiveHandoffOriginAsync(library, "Film/film.mkv", "h4");
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Conflict);

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.True(File.Exists(source));
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM file_skip_markers"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task A_handoff_manager_that_cannot_be_reached_for_the_keep_report_is_named_without_the_word_failed()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await GiveHandoffOriginAsync(library, "Film/film.mkv", "h5");
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, """{"capabilities":["processor-reject-regrab"],"libraries":[]}""");
        _fixture.Http.Throw(HttpMethod.Post, EventsPath, new HttpRequestException("no route to host"));

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.True(outcome.Message.Contains("(could not reach ", StringComparison.Ordinal), outcome.Message);
        Assert.EndsWith("), so Weir did not keep it.", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed: ", outcome.Message, StringComparison.Ordinal);
    }

    // --- a different release lands at the same path (#786 review of #785) ---------------------------------

    [Fact]
    public async Task Delete_refuses_a_file_that_changed_since_it_failed()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        // A new, different release lands at the same path after the title failed.
        File.WriteAllBytes(source, "a completely different release replaced this one"u8.ToArray());

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(
            "This file has changed since it failed, so Weir won't delete it. It will be looked at again on the next scan.",
            outcome.Message);
        Assert.True(File.Exists(source));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task Keep_refuses_a_file_that_changed_since_it_was_rejected()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source, "rejected");
        File.WriteAllBytes(source, "a completely different release replaced this one"u8.ToArray());

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(
            "This file has changed since it failed, so Weir won't delete it. It will be looked at again on the next scan.",
            outcome.Message);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM file_skip_markers"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task A_row_with_no_recorded_fingerprint_refuses_delete_rather_than_guess()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        // No fingerprint recorded: as for every row from before migration 0025.
        var file = await FileRowAsync(library, "Film/film.mkv", null);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(
            "This file changed after you opened this dialog. Close it and try again.",
            outcome.Message);
        Assert.True(File.Exists(source));
    }

    // --- a pre-upgrade row (#786 follow-up: migration 0025 only records a fingerprint going forward) ---------

    [Fact]
    public async Task A_row_with_no_recorded_fingerprint_offers_its_current_file_details_to_confirm()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", null);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));

        Assert.True(options.RequiresChoice);
        Assert.False(options.FingerprintRecorded);
        Assert.Equal(new FileInfo(source).Length, options.UnconfirmedSizeBytes);
        Assert.NotNull(options.UnconfirmedModifiedAt);
    }

    [Fact]
    public async Task A_row_with_no_recorded_fingerprint_deletes_when_the_confirmed_details_match()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", null);
        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        var confirmation = new FileRemovalConfirmation(options.UnconfirmedSizeBytes!.Value, options.UnconfirmedModifiedAt!);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, confirmation, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task A_row_with_no_recorded_fingerprint_refuses_delete_when_the_confirmed_details_no_longer_match()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", null);
        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        // A different release lands at the same path between the dialog opening and the owner confirming.
        File.WriteAllBytes(source, "a completely different release replaced this one"u8.ToArray());
        var confirmation = new FileRemovalConfirmation(options.UnconfirmedSizeBytes!.Value, options.UnconfirmedModifiedAt!);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, confirmation, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(
            "This file changed after you opened this dialog. Close it and try again.",
            outcome.Message);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_row_with_no_recorded_fingerprint_keeps_when_the_confirmed_details_match()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", null, "rejected");
        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        var confirmation = new FileRemovalConfirmation(options.UnconfirmedSizeBytes!.Value, options.UnconfirmedModifiedAt!);

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, confirmation, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.True(File.Exists(source));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM file_skip_markers"));
    }

    [Fact]
    public async Task A_row_with_no_recorded_fingerprint_refuses_keep_when_the_confirmed_details_no_longer_match()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", null, "rejected");
        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        File.WriteAllBytes(source, "a completely different release replaced this one"u8.ToArray());
        var confirmation = new FileRemovalConfirmation(options.UnconfirmedSizeBytes!.Value, options.UnconfirmedModifiedAt!);

        var outcome = await _fixture.Db(uow => Service().KeepAsync(uow, file, confirmation, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.Equal(
            "This file changed after you opened this dialog. Close it and try again.",
            outcome.Message);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM file_skip_markers"));
    }

    // --- the recorded-fingerprint paths are unchanged by the above (#786 follow-up) --------------------------

    [Fact]
    public async Task A_row_with_a_recorded_fingerprint_still_deletes_without_any_confirmation()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));
        Assert.True(options.FingerprintRecorded);
        Assert.Null(options.UnconfirmedSizeBytes);
        Assert.Null(options.UnconfirmedModifiedAt);

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.False(File.Exists(source));
    }

    // --- a stale "keep" marker must not outlive the file it was for (#786 review of #785) ------------------

    [Fact]
    public async Task Delete_clears_a_leftover_keep_marker_for_the_same_path()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        // A marker left from an earlier, different release at this same path.
        await _fixture.Db(async uow => { await _skipMarkers.SetAsync(uow, library, "Film/film.mkv", 1, 1); return 0; });

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.True(outcome.Done);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM file_skip_markers"));
    }

    [Fact]
    public async Task A_failed_title_whose_workflow_is_gone_does_not_qualify_for_a_choice()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        await _fixture.Store.Execute("DELETE FROM libraries");

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));

        Assert.False(options.RequiresChoice);
    }

    [Fact]
    public async Task A_handoff_manager_that_does_not_answer_its_manifest_cannot_take_the_rejection()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        await LinkAsync(library, connection);
        await GiveHandoffOriginAsync(library, "Film/film.mkv", "h5");
        _fixture.Http.Json(HttpMethod.Get, ManifestPath, "{}", HttpStatusCode.InternalServerError);

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));

        Assert.False(options.DeleteHandledByManager);
        Assert.Empty(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
    }

    [Fact]
    public async Task A_title_handed_over_by_a_manager_that_is_not_saved_is_removed_by_Weir_alone()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = await FileRowAsync(library, "Film/film.mkv", source);
        await GiveHandoffOriginAsync(library, "Film/film.mkv", "h6");

        var options = await _fixture.Db(uow => Service().EvaluateAsync(uow, file, CancellationToken.None));

        Assert.False(options.DeleteHandledByManager);
        Assert.Null(options.ManagerLabel);
    }

    [Fact]
    public async Task Delete_refuses_a_file_whose_fingerprint_was_only_half_recorded_when_nothing_confirms_it()
    {
        var library = await LibraryAsync();
        var source = _folders.Source("Film/film.mkv");
        var file = (await FileRowAsync(library, "Film/film.mkv", source)) with { FingerprintMtimeNs = null };

        var outcome = await _fixture.Db(uow => Service().DeleteAsync(uow, file, null, CancellationToken.None));

        Assert.False(outcome.Done);
        Assert.True(File.Exists(source));
    }
}
