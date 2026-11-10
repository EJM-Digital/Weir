using System.Globalization;
using System.Text.Json;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A hand-off of several files, and several hand-offs of one file, share copies in the hand-back folder. What the reply says, what
/// Activity records and what each copy keeps depend on which copies heard the word and which already had one standing. Real
/// database, real folders, real copies; the words come from real calls, with the stored state of a copy set only where Cleanup (not
/// a manager) made it.
/// </summary>
public sealed class HandbackOutcomesSharedCopyTests : IDisposable
{
    private const string Folder = "Film (2024)";
    private const string First = Folder + "/Film (2024) A.mkv";
    private const string Second = Folder + "/Film (2024) B.mkv";

    private readonly MediaManagerFixture _fixture = new();
    private readonly TempDirectory _folders = new();
    private readonly HandbackOutcomes _outcomes;
    private long _libraryId;

    public HandbackOutcomesSharedCopyTests()
    {
        _outcomes = new HandbackOutcomes(_fixture.Ledger, _fixture.Targets, _fixture.Handback, _fixture.Store.Clock);
    }

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private string CopyPath(string relative) => Path.Join(_folders.Join("hand-back"), relative);

    private async Task LibraryWithCopiesAsync(params string[] relatives)
    {
        _libraryId = await _fixture.LibraryAsync("movie", _folders.Join("watched"));
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '{_folders.Join("hand-back").Replace("'", "''", StringComparison.Ordinal)}' WHERE id = {_libraryId}");
        foreach (var relative in relatives)
        {
            var path = CopyPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[64]);
            await _fixture.Db(async uow =>
            {
                await _fixture.Handback.RecordWrittenAsync(uow, _libraryId, relative, path, _fixture.Store.Clock.GetUtcNow());
                return 0;
            });
        }
    }

    /// <summary>A hand-off of the folder whose passes finished and whose report named every one of <paramref name="relatives"/>.</summary>
    private async Task<HandoffLedgerRow> HandoffAsync(string handoffId, long? connectionId, params string[] relatives)
    {
        await _fixture.Db(async uow =>
        {
            var rowId = await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", handoffId, _libraryId, Folder, connectionId);
            await _fixture.Targets.AddAsync(uow, rowId, relatives);
            var row = (await HandoffLedgerStore.FindAsync(uow, "deluno", handoffId))!;
            foreach (var relative in relatives)
            {
                var finished = new WireObject().Set("ok", true).Set("outcome", "live_output_written").Set("relative_media_path", relative).Set("output_file", CopyPath(relative));
                await _fixture.Targets.FinishAsync(uow, row, relative, finished);
            }

            return 0;
        });
        return (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", handoffId)))!;
    }

    private Task<HandoffOutcomeResult> SayAsync(HandoffLedgerRow row, string manager, string outcome) =>
        _fixture.Db(uow => _outcomes.RecordHandoffOutcomeAsync(uow, row, manager, outcome, DateTimeOffset.UtcNow, "/library/Film (2024).mkv", null));

    private Task<HandbackRow?> CopyAsync(string relative) => _fixture.Db(uow => _fixture.Handback.FindAsync(uow, _libraryId, relative));

    private async Task<string> ActivityAsync(string column)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM activity_events WHERE event_type = 'processing.handback_outcome' ORDER BY id DESC LIMIT 1";
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<JsonElement> ActivityDetailAsync()
    {
        using var detail = JsonDocument.Parse(await ActivityAsync("detail"));
        return detail.RootElement.Clone();
    }

    [Fact]
    public async Task An_import_sent_again_after_it_released_the_copy_is_told_the_copy_was_already_released()
    {
        await LibraryWithCopiesAsync(First);
        var handoff = await HandoffAsync("h1", null, First);
        var first = await SayAsync(handoff, "Deluno", HandbackRules.Imported);
        Assert.True(first.Released);

        var again = await SayAsync(handoff, "Deluno", HandbackRules.Imported);

        Assert.False(again.Released);
        Assert.Equal(
            "Weir recorded that Deluno imported the file. Weir had already released its copy, so there was nothing for it to remove.",
            again.Message);
        var detail = await ActivityDetailAsync();
        Assert.False(detail.GetProperty("released").GetBoolean());
        Assert.Equal(0, detail.GetProperty("counts").GetProperty("removed").GetInt32());
        Assert.Equal(again.Message, detail.GetProperty("user_message").GetString());
    }

    [Fact]
    public async Task A_copy_that_settled_without_being_released_is_told_apart_and_the_first_note_of_several_is_the_one_given()
    {
        await LibraryWithCopiesAsync(First, Second);
        await SayAsync(await HandoffAsync("only-second", null, Second), "Deluno", HandbackRules.Imported);
        await _fixture.Store.Execute($"UPDATE handbacks SET settled_at = '2026-01-01 00:00:00', released_at = NULL, outcome = 'imported', outcome_by = 'Radarr', outcome_source_key = 'radarr', outcome_connection_id = 9, outcome_authenticated = 1 WHERE library_id = {_libraryId} AND relative_path = '{First}'");
        var both = await HandoffAsync("both", null, First, Second);

        var answer = await SayAsync(both, "Deluno", HandbackRules.Imported);

        Assert.False(answer.Released);
        Assert.Equal(
            $"Weir recorded that Deluno imported the file. {HandbackRules.StandingNote(released: false)}",
            answer.Message);
    }

    [Fact]
    public async Task A_copy_Weir_already_released_beside_one_it_releases_now_still_keeps_the_hand_off_from_saying_it_released_everything()
    {
        await LibraryWithCopiesAsync(First, Second);
        await SayAsync(await HandoffAsync("only-first", null, First), "Deluno", HandbackRules.Imported);
        var both = await HandoffAsync("both", null, First, Second);

        var answer = await SayAsync(both, "Deluno", HandbackRules.Imported);

        Assert.False(answer.Released);
        Assert.Equal(
            $"Weir recorded that Deluno imported the file. {HandbackRules.StandingNote(released: true)}",
            answer.Message);
        Assert.False(File.Exists(CopyPath(Second)));
        Assert.NotNull((await CopyAsync(Second))!.ReleasedAt);
    }

    [Fact]
    public async Task A_copy_that_was_removed_beside_one_the_manager_already_moved_is_still_a_release()
    {
        await LibraryWithCopiesAsync(First, Second);
        File.Delete(CopyPath(Second));
        var handoff = await HandoffAsync("h1", null, First, Second);

        var answer = await SayAsync(handoff, "Deluno", HandbackRules.Imported);

        Assert.True(answer.Released);
        Assert.Equal("Weir recorded that Deluno imported the file and released its copy.", answer.Message);
    }

    [Fact]
    public async Task Copies_that_are_all_already_gone_are_told_so_in_the_managers_words()
    {
        await LibraryWithCopiesAsync(First, Second);
        File.Delete(CopyPath(First));
        File.Delete(CopyPath(Second));
        var handoff = await HandoffAsync("h1", null, First, Second);

        var answer = await SayAsync(handoff, "Deluno", HandbackRules.Imported);

        Assert.False(answer.Released);
        Assert.Equal($"Weir recorded that Deluno imported the file. {HandbackRules.AlreadyGoneNote("Deluno")}", answer.Message);
    }

    [Fact]
    public async Task Of_several_copies_kept_for_their_own_reasons_the_reply_gives_the_first_one()
    {
        await LibraryWithCopiesAsync(First, Second);
        await File.WriteAllBytesAsync(CopyPath(First), new byte[999]);
        await _fixture.Store.Execute($"UPDATE handbacks SET settled_at = '2026-01-01 00:00:00', release_note = 'Cleanup kept it for its own reason.' WHERE library_id = {_libraryId} AND relative_path = '{Second}'");
        var handoff = await HandoffAsync("h1", null, First, Second);

        var answer = await SayAsync(handoff, "Deluno", HandbackRules.Imported);

        Assert.False(answer.Released);
        Assert.Equal($"Weir recorded that Deluno imported the file. {HandbackRules.ChangedNote}", answer.Message);
        Assert.True(File.Exists(CopyPath(First)));
    }

    [Fact]
    public async Task A_copy_Cleanup_removed_before_the_word_came_is_counted_as_removed_and_one_it_kept_gives_its_own_note()
    {
        await LibraryWithCopiesAsync(First, Second);
        await _fixture.Store.Execute(
            $"UPDATE handbacks SET settled_at = '2026-01-01 00:00:00', released_at = '2026-01-01 00:00:00', release_note = 'Cleanup removed it.' WHERE library_id = {_libraryId} AND relative_path = '{First}'");

        var removed = await SayAsync(await HandoffAsync("h1", null, First), "Deluno", HandbackRules.Imported);

        Assert.True(removed.Released);
        Assert.Equal("Weir recorded that Deluno imported the file and released its copy.", removed.Message);

        await _fixture.Store.Execute(
            $"UPDATE handbacks SET settled_at = '2026-01-01 00:00:00', released_at = NULL, release_note = 'Cleanup kept it for its own reason.' WHERE library_id = {_libraryId} AND relative_path = '{Second}'");

        var kept = await SayAsync(await HandoffAsync("h2", null, Second), "Deluno", HandbackRules.Imported);

        Assert.False(kept.Released);
        Assert.Equal("Weir recorded that Deluno imported the file. Cleanup kept it for its own reason.", kept.Message);
    }

    [WindowsFact("A file another program holds open can only be locked against deletion on Windows")]
    public async Task A_copy_in_use_when_the_import_arrives_is_recorded_as_kept_with_the_note_to_remove_it_by_hand()
    {
        await LibraryWithCopiesAsync(First);
        var handoff = await HandoffAsync("h1", null, First);

        HandoffOutcomeResult answer;
        await using (new FileStream(CopyPath(First), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            answer = await SayAsync(handoff, "Deluno", HandbackRules.Imported);
        }

        Assert.False(answer.Released);
        Assert.Contains("Weir could not remove its copy (", answer.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(CopyPath(First)));
        var copy = (await CopyAsync(First))!;
        Assert.NotNull(copy.SettledAt);
        Assert.Null(copy.ReleasedAt);
    }

    [Fact]
    public async Task A_refusal_from_another_connection_leaves_the_standing_refusal_and_is_not_told_as_already_imported()
    {
        await LibraryWithCopiesAsync(First);
        var firstConnection = await HandoffAsync("h1", 1, First);
        var otherConnection = await HandoffAsync("h2", 2, First);
        await SayAsync(firstConnection, "Deluno", HandbackRules.NotImported);

        var answer = await SayAsync(otherConnection, "Deluno", HandbackRules.NotImported);

        Assert.False(answer.Released);
        Assert.Equal("Weir recorded that the file will not be imported, and kept its copy.", answer.Message);
    }

    [Fact]
    public async Task A_refusal_of_files_imported_by_two_managers_names_the_first_one_that_imported()
    {
        await LibraryWithCopiesAsync(First, Second);
        await SayAsync(await HandoffAsync("h1", 1, First), "Radarr", HandbackRules.Imported);
        await SayAsync(await HandoffAsync("h2", 2, Second), "Sonarr", HandbackRules.Imported);
        var both = await HandoffAsync("h3", 3, First, Second);

        var answer = await SayAsync(both, "Deluno", HandbackRules.NotImported);

        Assert.Equal(
            "Weir recorded that the file will not be imported. Radarr has already imported it, so Weir left what it recorded about the file as it is.",
            answer.Message);
    }

    [Fact]
    public async Task An_import_that_lifts_two_refusals_at_once_is_still_told_as_imported_after_all()
    {
        await LibraryWithCopiesAsync(First, Second);
        var handoff = await HandoffAsync("h1", null, First, Second);
        await SayAsync(handoff, "Deluno", HandbackRules.NotImported);

        var answer = await SayAsync(handoff, "Deluno", HandbackRules.Imported);

        Assert.True(answer.Released);
        Assert.Equal("Deluno imported Film (2024) after all", await ActivityAsync("title"));
    }

    [Fact]
    public async Task The_activity_of_a_word_on_one_file_of_a_folder_names_that_file_and_a_word_on_two_names_the_folder()
    {
        await LibraryWithCopiesAsync(First, Second);

        await SayAsync(await HandoffAsync("one", null, First), "Deluno", HandbackRules.Imported);

        var single = await ActivityDetailAsync();
        Assert.Equal(First, single.GetProperty("relative_media_path").GetString());
        Assert.Equal(First, await ActivityAsync("relative_path"));
        Assert.Equal(_libraryId, single.GetProperty("library_id").GetInt64());
        Assert.Equal(_libraryId.ToString(CultureInfo.InvariantCulture), await ActivityAsync("library_id"));
        Assert.Equal("processing", single.GetProperty("module").GetString());
        Assert.Equal("processing", await ActivityAsync("module"));
        Assert.Equal(1, single.GetProperty("counts").GetProperty("removed").GetInt32());

        await SayAsync(await HandoffAsync("two", null, First, Second), "Deluno", HandbackRules.Imported);

        var both = await ActivityDetailAsync();
        Assert.Equal(Folder, both.GetProperty("relative_media_path").GetString());
        Assert.False(both.GetProperty("released").GetBoolean());
    }

    [Fact]
    public async Task A_word_on_a_hand_off_with_no_file_it_named_is_about_the_hand_offs_own_path()
    {
        await LibraryWithCopiesAsync(First);
        var handoff = await HandoffAsync("h1", null, First);
        await _fixture.Store.Execute($"UPDATE handbacks SET written_at = '2020-01-01 00:00:00' WHERE library_id = {_libraryId}");

        var answer = await SayAsync(handoff, "Deluno", HandbackRules.Imported);

        Assert.False(answer.Released);
        Assert.Equal(Folder, (await ActivityDetailAsync()).GetProperty("relative_media_path").GetString());
    }
}
