using System.Globalization;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A media manager's word on a file Weir handed back decides whether Weir deletes its copy. Real database, real folders and a real
/// copy on disk: who may release it, when it is only recorded, and what Activity and the reply say.
/// </summary>
public sealed class HandbackOutcomesTests : IDisposable
{
    private const string Relative = "Film (2024)/Film (2024).mkv";
    private static readonly ManagerSpeaker Signed = new("radarr", 4, Authenticated: true);
    private static readonly ManagerSpeaker Unsigned = new("radarr", 4, Authenticated: false);

    private readonly MediaManagerFixture _fixture = new();
    private readonly TempDirectory _folders = new();
    private readonly HandbackOutcomes _outcomes;
    private long _libraryId;

    public HandbackOutcomesTests()
    {
        _outcomes = new HandbackOutcomes(_fixture.Ledger, _fixture.Targets, _fixture.Handback, _fixture.Store.Clock);
    }

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private string OutputFolder => _folders.Join("hand-back");

    private string CopyPath => Path.Join(OutputFolder, "Film (2024)", "Film (2024).mkv");

    /// <summary>A library whose watched and output folders are real, and a copy Weir wrote into the output folder.</summary>
    private async Task HandedBackAsync()
    {
        _libraryId = await _fixture.LibraryAsync("movie", _folders.Join("watched"));
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '{OutputFolder.Replace("'", "''", StringComparison.Ordinal)}' WHERE id = {_libraryId}");
        Directory.CreateDirectory(Path.GetDirectoryName(CopyPath)!);
        await File.WriteAllBytesAsync(CopyPath, new byte[64]);
        await _fixture.Db(async uow =>
        {
            await _fixture.Handback.RecordWrittenAsync(uow, _libraryId, Relative, CopyPath, _fixture.Store.Clock.GetUtcNow());
            return 0;
        });
    }

    private static MediaManagerImportEvent Import(string? sourcePath = null, string? downloadId = null) => new()
    {
        SourceKey = "radarr",
        EventKind = MediaManagerImportEvent.Imported,
        MediaScope = "movie",
        FilePath = "/library/Film (2024)/Film (2024).mkv",
        SourcePath = sourcePath,
        DownloadId = downloadId,
    };

    private Task<ManagerImportResult> ImportAsync(MediaManagerImportEvent importEvent, ManagerSpeaker speaker) =>
        _fixture.Db(uow => _outcomes.RecordManagerImportAsync(uow, importEvent, "Radarr", speaker));

    private Task<HandbackRow?> CopyAsync() => _fixture.Db(uow => _fixture.Handback.FindAsync(uow, _libraryId, Relative));

    private async Task<string> ActivityAsync(string column)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM activity_events WHERE event_type = 'processing.handback_outcome' ORDER BY id DESC LIMIT 1";
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<long> ActivityCountAsync() =>
        await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.handback_outcome'");

    [Fact]
    public async Task An_import_of_a_file_weir_never_handed_back_records_nothing()
    {
        await HandedBackAsync();

        var result = await ImportAsync(Import(sourcePath: "/downloads/Someone Elses Film.mkv"), Signed);

        Assert.False(result.Matched);
        Assert.False(result.Released);
        Assert.Equal("Weir did not hand this file back, so there is nothing for it to record.", result.Message);
        Assert.Null((await CopyAsync())!.Outcome);
        Assert.True(File.Exists(CopyPath));
        Assert.Equal(0, await ActivityCountAsync());
    }

    [Fact]
    public async Task A_signed_import_named_by_the_copys_path_releases_weirs_copy_and_says_so_in_activity()
    {
        await HandedBackAsync();

        var result = await ImportAsync(Import(sourcePath: CopyPath), Signed);

        Assert.True(result.Matched);
        Assert.True(result.Released);
        Assert.Equal(HandbackRules.RemovedNote("Radarr"), result.Message);
        Assert.Equal(Relative, result.RelativePath);
        Assert.Equal(_libraryId, result.LibraryId);
        Assert.False(File.Exists(CopyPath));
        var copy = (await CopyAsync())!;
        Assert.Equal(HandbackRules.Imported, copy.Outcome);
        Assert.Equal("Radarr", copy.OutcomeBy);
        Assert.NotNull(copy.ReleasedAt);
        Assert.NotNull(copy.SettledAt);
        Assert.Equal(HandbackRules.RemovedNote("Radarr"), copy.ReleaseNote);
        Assert.Equal("Radarr imported Film (2024).mkv", await ActivityAsync("title"));
        var detail = await ActivityAsync("detail");
        Assert.Contains($"\"relative_media_path\":\"{Relative}\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"provider\":\"Radarr\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"imported\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"imported_path\":\"/library/Film (2024)/Film (2024).mkv\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"released\":true", detail, StringComparison.Ordinal);
        Assert.Contains("\"counts\":{\"removed\":1}", detail, StringComparison.Ordinal);
        Assert.Contains("\"trigger\":\"webhook\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"handback\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"success\"", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_import_named_by_a_path_that_ends_with_the_copys_place_below_the_output_folder_is_matched()
    {
        await HandedBackAsync();

        var result = await ImportAsync(Import(sourcePath: "/mnt/radarr/hand-back/Film (2024)/Film (2024).mkv"), Signed);

        Assert.True(result.Matched);
        Assert.True(result.Released);
    }

    [Fact]
    public async Task An_import_with_the_download_id_of_a_hand_off_finds_its_only_copy()
    {
        await HandedBackAsync();
        await HandoffAsync("h1", "ABC123");

        var result = await ImportAsync(Import(downloadId: " ABC123 "), Signed);

        Assert.True(result.Matched);
        Assert.Equal(Relative, result.RelativePath);
    }

    [Fact]
    public async Task A_download_id_that_covers_two_copies_names_neither_of_them()
    {
        await HandedBackAsync();
        var other = Path.Join(OutputFolder, "Other (2024)", "Other (2024).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        await File.WriteAllBytesAsync(other, new byte[32]);
        await _fixture.Db(async uow =>
        {
            await _fixture.Handback.RecordWrittenAsync(uow, _libraryId, "Other (2024)/Other (2024).mkv", other, _fixture.Store.Clock.GetUtcNow());
            return 0;
        });
        await HandoffAsync("h1", "SHARED");
        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "h2", _libraryId, "Other (2024)/Other (2024).mkv", downloadId: "SHARED");
            return 0;
        });

        var result = await ImportAsync(Import(downloadId: "SHARED"), Signed);

        Assert.False(result.Matched);
        Assert.True(File.Exists(CopyPath));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task An_import_that_cannot_be_told_to_come_from_the_manager_is_recorded_and_removes_nothing()
    {
        await HandedBackAsync();

        var result = await ImportAsync(Import(sourcePath: CopyPath), Unsigned);

        Assert.True(result.Matched);
        Assert.False(result.Released);
        Assert.Equal(HandbackRules.UnsignedNote("Radarr"), result.Message);
        Assert.True(File.Exists(CopyPath));
        var copy = (await CopyAsync())!;
        Assert.Equal(HandbackRules.Imported, copy.Outcome);
        Assert.Null(copy.ReleasedAt);
        Assert.Equal(HandbackRules.UnsignedNote("Radarr"), copy.ReleaseNote);
        Assert.Contains("\"released\":false", await ActivityAsync("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copy_that_changed_since_weir_wrote_it_is_kept()
    {
        await HandedBackAsync();
        await File.WriteAllBytesAsync(CopyPath, new byte[65]);

        var result = await ImportAsync(Import(sourcePath: CopyPath), Signed);

        Assert.False(result.Released);
        Assert.Equal(HandbackRules.ChangedNote, result.Message);
        Assert.True(File.Exists(CopyPath));
    }

    [Fact]
    public async Task A_copy_the_manager_already_moved_is_recorded_as_gone_and_nothing_is_removed()
    {
        await HandedBackAsync();
        File.Delete(CopyPath);

        var result = await ImportAsync(Import(sourcePath: CopyPath), Signed);

        Assert.False(result.Released);
        Assert.Equal(HandbackRules.AlreadyGoneNote("Radarr"), result.Message);
    }

    [Fact]
    public async Task The_same_signed_import_sent_again_gets_the_answer_it_got_and_a_second_caller_is_told_what_the_copy_says()
    {
        await HandedBackAsync();
        await ImportAsync(Import(sourcePath: CopyPath), Signed);
        var activityBefore = await ActivityCountAsync();

        var again = await ImportAsync(Import(sourcePath: CopyPath), Signed);
        var other = await ImportAsync(Import(sourcePath: CopyPath), new ManagerSpeaker("radarr", 9, Authenticated: true));

        Assert.True(again.Matched);
        Assert.True(again.Released);
        Assert.Equal(HandbackRules.RemovedNote("Radarr"), again.Message);
        Assert.True(other.Matched);
        Assert.False(other.Released);
        Assert.Equal(HandbackRules.StandingNote(released: true), other.Message);
        Assert.Equal(activityBefore, await ActivityCountAsync());
    }

    [Fact]
    public async Task An_unsigned_import_after_a_word_has_settled_the_copy_is_told_only_that_it_carries_no_secret()
    {
        await HandedBackAsync();
        await ImportAsync(Import(sourcePath: CopyPath), Signed);

        var result = await ImportAsync(Import(sourcePath: CopyPath), Unsigned);

        Assert.True(result.Matched);
        Assert.False(result.Released);
        Assert.Equal(HandbackRules.UnsignedNote("Radarr"), result.Message);
        Assert.Equal(HandbackRules.Imported, (await CopyAsync())!.Outcome);
    }

    [Fact]
    public async Task A_copy_that_weir_settled_on_its_own_keeps_what_happened_then_when_the_import_arrives()
    {
        await HandedBackAsync();
        await _fixture.Store.Execute(
            $"UPDATE handbacks SET settled_at = '2026-01-01 00:00:00', release_note = 'Weir removed its copy after 14 days.' WHERE library_id = {_libraryId}");

        var result = await ImportAsync(Import(sourcePath: CopyPath), Signed);

        Assert.True(result.Matched);
        Assert.False(result.Released);
        Assert.Equal("Weir removed its copy after 14 days.", result.Message);
        Assert.True(File.Exists(CopyPath));
        Assert.Equal(HandbackRules.Imported, (await CopyAsync())!.Outcome);
        Assert.Equal("Radarr imported Film (2024).mkv", await ActivityAsync("title"));
    }

    [Fact]
    public async Task A_copy_settled_without_a_note_says_there_is_no_record_of_what_became_of_it()
    {
        await HandedBackAsync();
        await _fixture.Store.Execute($"UPDATE handbacks SET settled_at = '2026-01-01 00:00:00', release_note = NULL WHERE library_id = {_libraryId}");

        var result = await ImportAsync(Import(sourcePath: CopyPath), Signed);

        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public async Task A_manager_that_will_not_import_a_file_has_its_refusal_recorded_and_weir_keeps_the_copy()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.NotImported, DateTimeOffset.UtcNow, null, "It is a sample."));

        Assert.False(answer.Released);
        Assert.Equal("Weir recorded that the file will not be imported, and kept its copy.", answer.Message);
        Assert.True(File.Exists(CopyPath));
        var copy = (await CopyAsync())!;
        Assert.Equal(HandbackRules.NotImported, copy.Outcome);
        Assert.Equal("Deluno", copy.OutcomeBy);
        Assert.Equal("It is a sample.", copy.OutcomeReason);
        Assert.Equal(HandbackRules.NotImportedNote("Deluno", "It is a sample."), copy.ReleaseNote);
        Assert.Equal("Deluno will not import Film (2024).mkv", await ActivityAsync("title"));
        Assert.Contains("\"reason\":\"It is a sample.\"", await ActivityAsync("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_managers_import_of_a_handed_off_file_releases_the_copy_it_named_and_the_reply_says_so()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.Imported, DateTimeOffset.UtcNow, "/library/Film (2024).mkv", null));

        Assert.True(answer.Released);
        Assert.Equal("Weir recorded that Deluno imported the file and released its copy.", answer.Message);
        Assert.False(File.Exists(CopyPath));
        Assert.Equal("Deluno imported Film (2024).mkv", await ActivityAsync("title"));
    }

    [Fact]
    public async Task An_import_after_a_refusal_from_the_same_kind_of_manager_is_told_in_activity_as_imported_after_all()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");
        await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(uow, handoff, "Deluno", HandbackRules.NotImported, DateTimeOffset.UtcNow, null, null));

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.Imported, DateTimeOffset.UtcNow, "/library/Film (2024).mkv", null));

        Assert.True(answer.Released);
        Assert.Equal("Deluno imported Film (2024).mkv after all", await ActivityAsync("title"));
        using var detail = System.Text.Json.JsonDocument.Parse(await ActivityAsync("detail"));
        Assert.StartsWith(
            "Deluno had said it would not import this file, and then imported it after all.",
            detail.RootElement.GetProperty("user_message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_never_changes_a_copy_that_has_been_imported()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");
        await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.Imported, DateTimeOffset.UtcNow, "/library/Film (2024).mkv", null));

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.NotImported, DateTimeOffset.UtcNow, null, "Changed its mind."));

        Assert.False(answer.Released);
        Assert.Equal(
            "Weir recorded that the file will not be imported. Deluno has already imported it, so Weir left what it recorded about the file as it is.",
            answer.Message);
        Assert.Equal(HandbackRules.Imported, (await CopyAsync())!.Outcome);
    }

    [Fact]
    public async Task A_copy_the_report_never_named_is_left_untouched_by_the_outcome()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1", reported: false);

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.Imported, DateTimeOffset.UtcNow, null, null));

        Assert.False(answer.Released);
        Assert.Null((await CopyAsync())!.Outcome);
        Assert.True(File.Exists(CopyPath));
        Assert.Equal("Deluno imported Film (2024).mkv", await ActivityAsync("title"));
    }

    [Fact]
    public async Task A_hand_off_is_unanswered_while_a_copy_its_report_named_has_no_word_on_it()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");

        Assert.True(await _fixture.Db(uow => _outcomes.HasUnansweredCopyAsync(uow, handoff)));

        await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.NotImported, DateTimeOffset.UtcNow, null, null));

        Assert.False(await _fixture.Db(uow => _outcomes.HasUnansweredCopyAsync(uow, handoff)));
    }

    /// <summary>
    /// A hand-off of the copy whose pass finished and whose completion report named the copy as written then; with
    /// <paramref name="reported"/> false the file's pass has not finished, so no report named it.
    /// </summary>
    private async Task<HandoffLedgerRow> HandoffAsync(string handoffId, string downloadId, bool reported = true)
    {
        await _fixture.Db(async uow =>
        {
            var rowId = await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", handoffId, _libraryId, Relative, downloadId: downloadId);
            await _fixture.Targets.AddAsync(uow, rowId, [Relative]);
            if (reported)
            {
                var row = (await HandoffLedgerStore.FindAsync(uow, "deluno", handoffId))!;
                var finished = new WireObject().Set("ok", true).Set("outcome", "live_output_written").Set("relative_media_path", Relative).Set("output_file", CopyPath);
                await _fixture.Targets.FinishAsync(uow, row, Relative, finished);
            }

            return 0;
        });
        return (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", handoffId)))!;
    }

    [Fact]
    public async Task The_same_manager_telling_Weir_again_after_the_note_on_the_copy_was_lost_is_given_no_note()
    {
        await HandedBackAsync();
        await ImportAsync(Import(sourcePath: CopyPath), Signed);
        await _fixture.Store.Execute($"UPDATE handbacks SET release_note = NULL WHERE library_id = {_libraryId}");

        var again = await ImportAsync(Import(sourcePath: CopyPath), Signed);

        Assert.True(again.Matched);
        Assert.True(again.Released);
        Assert.Equal(string.Empty, again.Message);
    }

    [Fact]
    public async Task A_managers_word_on_a_hand_off_that_belongs_to_no_workflow_is_recorded_and_touches_no_copy()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff with { LibraryId = null }, "Deluno", HandbackRules.Imported, DateTimeOffset.UtcNow, "/library/Film (2024).mkv", null));

        Assert.False(answer.Released);
        Assert.True(File.Exists(CopyPath));
        Assert.Null((await CopyAsync())!.Outcome);
    }

    [Fact]
    public async Task A_refusal_with_nobody_on_record_as_having_said_it_is_taken_back_by_the_hand_off_that_asks()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");
        await _fixture.Store.Execute($"UPDATE handbacks SET outcome = '{HandbackRules.NotImported}', outcome_by = 'Deluno' WHERE library_id = {_libraryId}");

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.Imported, DateTimeOffset.UtcNow, "/library/Film (2024).mkv", null));

        Assert.True(answer.Released);
        Assert.False(File.Exists(CopyPath));
    }

    [Fact]
    public async Task A_hand_off_that_refused_takes_its_refusal_back_from_a_copy_that_does_not_say_who_refused()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");
        await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(uow, handoff, "Deluno", HandbackRules.NotImported, DateTimeOffset.UtcNow, null, null));
        await _fixture.Store.Execute(
            "UPDATE handbacks SET outcome_source_key = NULL, outcome_connection_id = NULL, outcome_authenticated = 1 " +
            $"WHERE library_id = {_libraryId}");
        var refused = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "h1")))!;
        Assert.Equal(HandbackRules.NotImported, refused.Outcome);

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, refused, "Deluno", HandbackRules.Imported, DateTimeOffset.UtcNow, "/library/Film (2024).mkv", null));

        Assert.True(answer.Released);
        Assert.False(File.Exists(CopyPath));
    }

    [Fact]
    public async Task An_import_after_a_refusal_with_nobody_on_record_as_having_said_it_is_told_as_imported_after_all()
    {
        await HandedBackAsync();
        await _fixture.Store.Execute($"UPDATE handbacks SET outcome = '{HandbackRules.NotImported}', outcome_by = 'Radarr' WHERE library_id = {_libraryId}");

        var result = await ImportAsync(Import(sourcePath: CopyPath), Signed);

        Assert.True(result.Released);
        Assert.Equal("Radarr imported Film (2024).mkv after all", await ActivityAsync("title"));
    }

    [Fact]
    public async Task A_copy_the_cleanup_job_kept_and_wrote_no_note_for_is_answered_with_the_unrecorded_note()
    {
        await HandedBackAsync();
        var handoff = await HandoffAsync("h1", "d1");
        await _fixture.Store.Execute($"UPDATE handbacks SET settled_at = '2026-01-01 00:00:00', released_at = NULL, release_note = NULL WHERE library_id = {_libraryId}");

        var answer = await _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(
            uow, handoff, "Deluno", HandbackRules.Imported, DateTimeOffset.UtcNow, "/library/Film (2024).mkv", null));

        Assert.False(answer.Released);
        Assert.True(File.Exists(CopyPath));
        Assert.Contains(HandbackRules.UnrecordedNote, answer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copy_in_a_workflow_with_no_watched_folder_is_released_by_the_output_folder_alone()
    {
        await HandedBackAsync();
        var row = (await CopyAsync())! with { WatchedFolder = " " };

        var release = HandbackStore.Release(row, "Radarr");

        Assert.True(release.Removed);
        Assert.False(File.Exists(CopyPath));
    }
}
