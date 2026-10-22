using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// What Weir does with a manager's word on a file it handed back, with real copies in a real output folder: which copy a message
/// is about, what a repeat of the same message gets, and which hand-offs still have a copy nobody has answered for.
/// </summary>
public sealed class HandbackOutcomesEdgeTests : IDisposable
{
    private const string Folder = "Example.Movie.2024.1080p";
    private const string Main = Folder + "/Example.Movie.2024.1080p.mkv";

    private static readonly ManagerSpeaker Radarr = new("radarr", 1, Authenticated: true);

    private readonly MediaManagerFixture _fixture = new();
    private long _movies;
    private long _shows;

    public void Dispose() => _fixture.Dispose();

    private HandbackOutcomes Outcomes() => new(_fixture.Ledger, _fixture.Targets, _fixture.Handback, _fixture.Store.Clock);

    private string Output(string library) => _fixture.Store.Home.Join(library + "-out");

    private async Task SetUpLibrariesAsync()
    {
        _movies = await _fixture.LibraryAsync("movie", _fixture.Store.Home.Join("movies"));
        _shows = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("shows"));
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '{Output("movies").Replace("'", "''", StringComparison.Ordinal)}' WHERE id = {_movies}");
        await _fixture.Store.Execute($"UPDATE libraries SET output_folder = '{Output("shows").Replace("'", "''", StringComparison.Ordinal)}' WHERE id = {_shows}");
    }

    /// <summary>A copy Weir wrote below the library's output folder, recorded as a pass records it.</summary>
    private async Task<string> CopyAsync(long library, string relative, string output, string writtenAt = "2026-01-10 09:00:00")
    {
        var path = Path.Join(output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "cleaned " + relative);
        var info = new FileInfo(path);
        await _fixture.Store.Execute(
            "INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at) VALUES " +
            $"({library}, '{relative}', '{path.Replace("'", "''", StringComparison.Ordinal)}', {info.Length}, {(info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100}, '{writtenAt}')");
        return path;
    }

    private static MediaManagerImportEvent Imported(string sourcePath, string scope = "movie", string? downloadId = null) => new()
    {
        SourceKey = "radarr",
        EventKind = MediaManagerImportEvent.Imported,
        MediaScope = scope,
        FilePath = "/library/movies/Example Movie (2024)/Example Movie (2024).mkv",
        SourcePath = sourcePath,
        DownloadId = downloadId,
    };

    private Task<ManagerImportResult> ImportAsync(MediaManagerImportEvent importEvent, ManagerSpeaker? speaker = null) =>
        _fixture.Db(uow => Outcomes().RecordManagerImportAsync(uow, importEvent, "Radarr", speaker ?? Radarr));

    private async Task<HandoffLedgerRow> ReceiveAsync(string? downloadId = null)
    {
        var watched = _fixture.Store.Home.Join("movies");
        Directory.CreateDirectory(Path.Join(watched, Folder));
        await File.WriteAllTextAsync(Path.Join(watched, Main), "main");
        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, new MediaManagerImportEvent
        {
            SourceKey = "deluno",
            EventKind = MediaManagerImportEvent.Handoff,
            MediaScope = "movie",
            FilePath = Path.Join(watched, Folder),
            HandoffId = "release",
            LibraryId = "lib-1",
            DownloadId = downloadId,
        }));
        return (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "release")))!;
    }

    [Fact]
    public void The_outcomes_cannot_be_built_without_their_parts()
    {
        var clock = _fixture.Store.Clock;
        Assert.Throws<ArgumentNullException>(() => new HandbackOutcomes(null!, _fixture.Targets, _fixture.Handback, clock));
        Assert.Throws<ArgumentNullException>(() => new HandbackOutcomes(_fixture.Ledger, null!, _fixture.Handback, clock));
        Assert.Throws<ArgumentNullException>(() => new HandbackOutcomes(_fixture.Ledger, _fixture.Targets, null!, clock));
        Assert.Throws<ArgumentNullException>(() => new HandbackOutcomes(_fixture.Ledger, _fixture.Targets, _fixture.Handback, null!));
    }

    [Fact]
    public async Task Every_entry_point_refuses_a_missing_argument()
    {
        var outcomes = Outcomes();
        await SetUpLibrariesAsync();
        var row = await ReceiveAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => outcomes.RecordManagerImportAsync(null!, Imported("/x/a.mkv"), "Radarr", Radarr));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _fixture.Db(uow => outcomes.RecordManagerImportAsync(uow, null!, "Radarr", Radarr)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _fixture.Db(uow => outcomes.RecordManagerImportAsync(uow, Imported("/x/a.mkv"), "Radarr", null!)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => outcomes.RecordHandoffOutcomeAsync(null!, row, "Deluno", "imported", DateTimeOffset.UnixEpoch, null, null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _fixture.Db(uow => outcomes.RecordHandoffOutcomeAsync(uow, null!, "Deluno", "imported", DateTimeOffset.UnixEpoch, null, null)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => outcomes.HasUnansweredCopyAsync(null!, row));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _fixture.Db(uow => outcomes.HasUnansweredCopyAsync(uow, null!)));
    }

    [Fact]
    public async Task A_file_Weir_never_handed_back_changes_nothing()
    {
        await SetUpLibrariesAsync();
        await CopyAsync(_movies, Main, Output("movies"));

        var noName = await ImportAsync(Imported("/"));
        var noId = await ImportAsync(Imported("/mnt/out/other/film.mkv"));

        Assert.False(noName.Matched);
        Assert.False(noId.Matched);
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM handbacks WHERE outcome IS NOT NULL"));
    }

    [Fact]
    public async Task An_import_of_a_copy_Weir_had_already_dealt_with_is_recorded_and_what_happened_then_stands()
    {
        await SetUpLibrariesAsync();
        var copy = await CopyAsync(_movies, Main, Output("movies"));
        await _fixture.Store.Execute(
            "UPDATE handbacks SET settled_at = '2026-01-11 09:00:00', release_note = 'Weir removed its copy when the Cleanup job found nobody had claimed it.'");

        var result = await ImportAsync(Imported(copy));

        Assert.True(result.Matched);
        Assert.False(result.Released);
        Assert.Equal("Weir removed its copy when the Cleanup job found nobody had claimed it.", result.Message);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND outcome_by = 'Radarr'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.handback_outcome'"));
    }

    [Fact]
    public async Task An_import_of_a_dealt_with_copy_that_left_no_note_has_nothing_to_say_about_it()
    {
        await SetUpLibrariesAsync();
        var copy = await CopyAsync(_movies, Main, Output("movies"));
        await _fixture.Store.Execute("UPDATE handbacks SET settled_at = '2026-01-11 09:00:00'");

        var result = await ImportAsync(Imported(copy));

        Assert.True(result.Matched);
        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public async Task The_same_manager_telling_Weir_again_gets_the_answer_it_got_and_a_stranger_is_told_where_things_stand()
    {
        await SetUpLibrariesAsync();
        var copy = await CopyAsync(_movies, Main, Output("movies"));
        var first = await ImportAsync(Imported(copy));
        Assert.True(first.Released);
        Assert.False(File.Exists(copy));

        var again = await ImportAsync(Imported(copy));
        var stranger = await ImportAsync(Imported(copy), new ManagerSpeaker("radarr", 2, Authenticated: true));
        var unsigned = await ImportAsync(Imported(copy), new ManagerSpeaker("radarr", null, Authenticated: false));

        Assert.True(again.Released);
        Assert.Equal(first.Message, again.Message);
        Assert.False(stranger.Released);
        Assert.Equal(HandbackRules.StandingNote(released: true), stranger.Message);
        Assert.False(unsigned.Released);
        Assert.Equal(HandbackRules.UnsignedNote("Radarr"), unsigned.Message);
    }

    [Fact]
    public async Task An_import_without_the_webhook_secret_is_recorded_but_removes_nothing()
    {
        await SetUpLibrariesAsync();
        var copy = await CopyAsync(_movies, Main, Output("movies"));

        var result = await ImportAsync(Imported(copy), new ManagerSpeaker("radarr", null, Authenticated: false));

        Assert.True(result.Matched);
        Assert.False(result.Released);
        Assert.True(File.Exists(copy));
        Assert.Equal(HandbackRules.UnsignedNote("Radarr"), result.Message);
    }

    [Fact]
    public async Task With_two_copies_of_one_file_name_the_kind_of_library_decides_and_otherwise_the_newest_still_looked_after()
    {
        await SetUpLibrariesAsync();
        const string Episode = "Show/Season 1/Show.S01E01.mkv";
        var movieCopy = await CopyAsync(_movies, Episode, Output("movies"), writtenAt: "2026-01-10 09:00:00");
        var showCopy = await CopyAsync(_shows, Episode, Output("shows"), writtenAt: "2026-01-09 09:00:00");
        const string Source = "/mnt/weir-out/Show/Season 1/Show.S01E01.mkv";

        var tv = await ImportAsync(Imported(Source, scope: "tv"));
        Assert.Equal(_shows, tv.LibraryId);
        Assert.False(File.Exists(showCopy));
        Assert.True(File.Exists(movieCopy));

        await ForgetEveryWordAsync();
        await _fixture.Store.Execute($"UPDATE libraries SET media_type = 'movie' WHERE id = {_shows}");
        var newest = await ImportAsync(Imported(Source, scope: "movie"));
        Assert.Equal(_movies, newest.LibraryId);

        await ForgetEveryWordAsync();
        var noneOfTheKind = await ImportAsync(Imported(Source, scope: "tv"));
        Assert.Equal(_movies, noneOfTheKind.LibraryId);

        await ForgetEveryWordAsync();
        await _fixture.Store.Execute($"UPDATE handbacks SET settled_at = '2026-01-12 09:00:00' WHERE library_id = {_movies}");
        var settledLast = await ImportAsync(Imported(Source, scope: "movie"));
        Assert.Equal(_shows, settledLast.LibraryId);
    }

    private Task ForgetEveryWordAsync() => _fixture.Store.Execute(
        "UPDATE handbacks SET outcome = NULL, outcome_by = NULL, outcome_at = NULL, released_at = NULL, settled_at = NULL, release_note = NULL, outcome_source_key = NULL, outcome_connection_id = NULL");

    [Fact]
    public async Task A_message_with_no_path_is_matched_by_the_download_it_was_for()
    {
        await SetUpLibrariesAsync();
        await ReceiveAsync("dl-1");
        var copy = await CopyAsync(_movies, Main, Output("movies"));

        var result = await ImportAsync(Imported(sourcePath: " ", downloadId: " dl-1 "));

        Assert.True(result.Matched);
        Assert.Equal(Main, result.RelativePath);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public async Task A_download_that_covers_more_than_one_copy_cannot_say_which_file_the_message_is_about()
    {
        await SetUpLibrariesAsync();
        await ReceiveAsync("dl-1");
        await _fixture.Store.Execute("DELETE FROM media_manager_handoff_targets");
        await CopyAsync(_movies, Main, Output("movies"));
        await _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status) VALUES ({_movies}, '{Folder}/extra.mkv', 'processed')");
        await CopyAsync(_movies, Folder + "/extra.mkv", Output("movies"));

        var result = await ImportAsync(Imported(sourcePath: string.Empty, downloadId: "dl-1"));

        Assert.False(result.Matched);
    }

    [Fact]
    public async Task A_download_nobody_handed_off_or_a_hand_off_in_no_workflow_matches_nothing()
    {
        await SetUpLibrariesAsync();
        await ReceiveAsync("dl-1");
        await CopyAsync(_movies, Main, Output("movies"));
        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET library_id = NULL");

        Assert.False((await ImportAsync(Imported(sourcePath: string.Empty, downloadId: "dl-1"))).Matched);
        Assert.False((await ImportAsync(Imported(sourcePath: string.Empty, downloadId: "other"))).Matched);
        Assert.False((await ImportAsync(Imported(sourcePath: string.Empty, downloadId: " "))).Matched);
    }

    [Fact]
    public async Task A_hand_off_has_an_unanswered_copy_until_the_manager_has_spoken_for_it()
    {
        await SetUpLibrariesAsync();
        var row = await ReceiveAsync();
        await _fixture.Store.Execute("DELETE FROM media_manager_handoff_targets");
        await CopyAsync(_movies, Main, Output("movies"));

        Assert.True(await _fixture.Db(uow => Outcomes().HasUnansweredCopyAsync(uow, row with { RelativePath = Main })));

        await _fixture.Store.Execute("UPDATE handbacks SET outcome = 'imported'");

        Assert.False(await _fixture.Db(uow => Outcomes().HasUnansweredCopyAsync(uow, row with { RelativePath = Main })));
        Assert.False(await _fixture.Db(uow => Outcomes().HasUnansweredCopyAsync(uow, row with { LibraryId = null })));
    }
}
