using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Jobs;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Activity;

/// <summary>
/// Activity history reads, commit-time notification and the paging and date-filter rules of the history endpoints.
/// </summary>
public sealed class ActivityHistoryStoreTests
{
    private readonly ActivityHistoryStore _history = new();

    [Fact]
    public async Task Record_activity_event_notifies_only_after_commit()
    {
        using var fixture = new StoreFixture();
        var notifier = ActivityNotifications.For(fixture.Database);
        var uow = await UnitOfWork.OpenAsync(fixture.Database);
        await using (uow)
        {
            var id = await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(ActivityEventTypes.AuthLoginSucceeded, "auth", "Commit-time notify test", "alice"));
            await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(ActivityEventTypes.AuthLogout, "auth", "Second", "alice"));
            Assert.Equal(new ActivityLatest(null, 0), notifier.Snapshot());
            await uow.CommitAsync();
            Assert.Equal(new ActivityLatest(id + 1, 1), notifier.Snapshot());

            await uow.CommitAsync();
            Assert.Equal(1, notifier.Snapshot().Version);
        }
    }

    [Fact]
    public async Task A_rollback_notifies_nobody()
    {
        using var fixture = new StoreFixture();
        var notifier = ActivityNotifications.For(fixture.Database);
        var uow = await UnitOfWork.OpenAsync(fixture.Database);
        await using (uow)
        {
            await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(ActivityEventTypes.AuthLogout, "auth", "Rolled back", "alice"));
            await uow.RollbackAsync();
            await uow.CommitAsync();
        }

        Assert.Equal(new ActivityLatest(null, 0), notifier.Snapshot());
        Assert.Equal(0, await fixture.Scalar("SELECT count(*) FROM activity_events WHERE title = 'Rolled back'"));
    }

    [Fact]
    public async Task Update_activity_event_reclassifies_and_notifies_the_same_row_after_commit()
    {
        using var fixture = new StoreFixture();
        var notifier = ActivityNotifications.For(fixture.Database);
        var writer = new SqliteActivityWriter(fixture.Database);
        var id = await writer.RecordAsync(new ActivityEventDraft(ActivityEventTypes.ProcessingFileProcessingProgress, "processing", "Processing movie.mkv", "{\"percent\":10}"));
        Assert.Equal(new ActivityLatest(id, 1), notifier.Snapshot());

        var uow = await UnitOfWork.OpenAsync(fixture.Database);
        await using (uow)
        {
            Assert.True(await SqliteActivityWriter.UpdateAsync(uow, id, eventType: ActivityEventTypes.ProcessingFileRemuxPassCompleted, detail: "{\"percent\":42,\"ok\":false,\"relative_media_path\":\"m.mkv\"}"));
            Assert.False(await SqliteActivityWriter.UpdateAsync(uow, id + 100, title: "missing"));
            Assert.Equal(new ActivityLatest(id, 1), notifier.Snapshot());
            await uow.CommitAsync();
        }

        Assert.Equal(new ActivityLatest(id, 2), notifier.Snapshot());
        Assert.Equal(1, await fixture.Scalar(
            $"SELECT count(*) FROM activity_events WHERE id = {id} AND title = 'Processing movie.mkv' AND result = 'failed' AND relative_path = 'm.mkv' AND event_type = 'processing.file_remux_pass_completed'"));
    }

    [Fact]
    public async Task Activity_written_in_a_job_store_transaction_notifies_after_its_commit()
    {
        using var db = new JobsTestDatabase(keepSeedRows: true);
        var notifier = ActivityNotifications.For(db.Database);
        var id = await db.Store.InTransactionAsync((connection, transaction) =>
        {
            var inserted = SqliteActivityWriter.Record(connection, transaction, new ActivityEventDraft("processing.x_completed", "processing", "raw", null));
            Assert.Equal(0, notifier.Snapshot().Version);
            return inserted;
        });
        Assert.Equal(new ActivityLatest(id, 1), notifier.Snapshot());
    }

    [Fact]
    public async Task The_latest_poll_notifies_of_a_row_written_on_a_connection_the_notifier_never_saw_commit()
    {
        // A raw insert with no unit of work and no ActivityNotifications.Track call, like `weir recover`'s
        // own connection or a restored backup: only the poll, not a commit signal, can tell the notifier.
        // The poll only checks while a stream is open, so this needs one waiting.
        //
        // The wait uses a clock that never advances on its own instead of TimeProvider.System: a real timeout
        // timer races the poll under CI thread-pool contention and can fire before the poll ever runs, failing
        // the test even though nothing is actually racy (WaitForChangeAsync registers the waiter synchronously,
        // before this method's first await, so the poll below always sees it).
        using var fixture = new StoreFixture();
        var notifier = new ActivityLatestNotifier();
        var poll = new ActivityLatestPollTask(fixture.Database, notifier, _history);
        var clock = new FakeTimeProvider();
        var waiting = notifier.WaitForChangeAsync(notifier.Snapshot().Version, TimeSpan.FromSeconds(5), clock);
        Assert.Equal(1, notifier.WaiterCount);

        await fixture.Execute("INSERT INTO activity_events (event_type, module, title) VALUES ('auth.password_changed', 'auth', 'Password changed')");
        var insertedId = await fixture.Scalar("SELECT max(id) FROM activity_events");
        await poll.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ActivityLatest(insertedId, 1), await waiting);
    }

    [Fact]
    public async Task The_latest_poll_does_nothing_when_the_latest_id_has_not_moved()
    {
        using var fixture = new StoreFixture();
        var notifier = new ActivityLatestNotifier();
        var poll = new ActivityLatestPollTask(fixture.Database, notifier, _history);
        await fixture.Execute("INSERT INTO activity_events (event_type, module, title) VALUES ('auth.password_changed', 'auth', 'Password changed')");
        var firstWait = notifier.WaitForChangeAsync(notifier.Snapshot().Version, TimeSpan.FromSeconds(5), new FakeTimeProvider());
        await poll.RunOnceAsync(CancellationToken.None);
        var afterFirstPoll = await firstWait.WaitAsync(TimeSpan.FromMinutes(1));

        var secondWait = notifier.WaitForChangeAsync(afterFirstPoll!.Value.Version, TimeSpan.FromMilliseconds(50), TimeProvider.System);
        await poll.RunOnceAsync(CancellationToken.None);

        Assert.Null(await secondWait);
        Assert.Equal(afterFirstPoll, notifier.Snapshot());
    }

    [Fact]
    public async Task The_latest_poll_tells_nobody_when_there_is_no_activity_yet()
    {
        using var fixture = new StoreFixture();
        var notifier = new ActivityLatestNotifier();
        var poll = new ActivityLatestPollTask(fixture.Database, notifier, _history);
        var clock = new FakeTimeProvider();
        var waiting = notifier.WaitForChangeAsync(notifier.Snapshot().Version, TimeSpan.FromSeconds(5), clock);

        await poll.RunOnceAsync(CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        Assert.Null(await _history.LatestIdAsync(fixture.Database));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await waiting);
    }

    [Fact]
    public async Task The_latest_poll_checks_nothing_while_no_stream_is_open()
    {
        using var fixture = new StoreFixture();
        var notifier = new ActivityLatestNotifier();
        var poll = new ActivityLatestPollTask(fixture.Database, notifier, _history);
        await fixture.Execute("INSERT INTO activity_events (event_type, module, title) VALUES ('auth.password_changed', 'auth', 'Password changed')");

        await poll.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ActivityLatest(null, 0), notifier.Snapshot());
    }

    [Fact]
    public void Csv_quotes_only_what_the_excel_dialect_quotes()
    {
        var row = new ActivityEventRow(7, Timestamp.Naive(new DateTime(2026, 1, 2, 3, 4, 5)), "a.b", "processing", "comma, \"quote\"", "line\nbreak", null, null, null, "plain;tab\t", null);
        Assert.Equal(
            "id,created_at,module,event_type,trigger,result,library_id,relative_path,title,detail\r\n" +
            "7,2026-01-02T03:04:05,processing,a.b,,,,plain;tab\t,\"comma, \"\"quote\"\"\",\"line\nbreak\"\r\n",
            ActivityHistory.ExportCsv([row]));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://example.invalid\")")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(1)")]
    [InlineData("\tindented")]
    public void Csv_writes_a_cell_a_spreadsheet_would_run_as_text(string title)
    {
        var row = new ActivityEventRow(7, Timestamp.Naive(new DateTime(2026, 1, 2, 3, 4, 5)), "a.b", "processing", title, null, null, null, null, "Film/film.mkv", null);

        var line = ActivityHistory.ExportCsv([row]).Split("\r\n")[1];

        Assert.Contains("'" + title.Replace("\"", "\"\"", StringComparison.Ordinal), line, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_leaves_ordinary_cells_as_they_are()
    {
        var row = new ActivityEventRow(7, Timestamp.Naive(new DateTime(2026, 1, 2, 3, 4, 5)), "a.b", "processing", "Film (2020) - 1080p", null, null, null, null, "Film/film.mkv", null);

        Assert.Equal(
            "7,2026-01-02T03:04:05,processing,a.b,,,,Film/film.mkv,Film (2020) - 1080p,",
            ActivityHistory.ExportCsv([row]).Split("\r\n")[1]);
    }

    /// <summary>#543 item 1: the count covers every row even with no filter, not one row.</summary>
    [Fact]
    public async Task Count_activity_events_counts_every_row_even_unfiltered()
    {
        using var fixture = new StoreFixture();
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title) VALUES " +
            "('2026-01-01 00:00:01', 'a.1', 'processing', 'one'), " +
            "('2026-01-01 00:00:02', 'a.2', 'processing', 'two'), " +
            "('2026-01-01 00:00:03', 'a.3', 'processing', 'three')");

        Assert.Equal(3, await fixture.WithUnitOfWork(uow => _history.CountAsync(uow, ActivityFilter.None)));

        var (total, hasMore, pageCount) = await fixture.WithUnitOfWork(async uow =>
        {
            var page = await _history.ListRecentAsync(uow, ActivityFilter.None, limit: 2, beforeId: null);
            var count = await _history.CountAsync(uow, ActivityFilter.None);
            var body = ActivityHistory.RecentOut(page.Items, page.HasMore, count, 90, null, new Dictionary<(long, string), string>());
            using var doc = JsonDocument.Parse(WireJsonWriter.DumpsUtf8(body, WireJsonFormat.Response));
            return (doc.RootElement.GetProperty("total").GetInt64(), doc.RootElement.GetProperty("has_more").GetBoolean(), page.Items.Count);
        });
        Assert.Equal(3, total);
        Assert.True(hasMore);
        Assert.Equal(2, pageCount);
    }

    /// <summary>
    /// #543 item 3: ordered by <c>(created_at DESC, id DESC)</c>, and <c>before_id</c> pages by that same key.
    /// Row 2 is chronologically the oldest despite its small id, and rows 1 and 3 tie — an arrangement plain
    /// <c>id &lt; before_id</c> paging gets wrong: it would repeat row 4 on
    /// a later page (its id is small, but it was already returned) or lose rows entirely.
    /// </summary>
    [Fact]
    public async Task List_recent_pages_by_before_id_without_skipping_or_repeating_a_tied_row()
    {
        using var fixture = new StoreFixture();
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title) VALUES " +
            "('2026-01-01 00:00:10', 'a.1', 'processing', 'A'), " + // id 1, ties id 3
            "('2026-01-01 00:00:05', 'a.2', 'processing', 'B'), " + // id 2, older than id 1 and id 3
            "('2026-01-01 00:00:10', 'a.3', 'processing', 'C'), " + // id 3, ties id 1
            "('2026-01-01 00:00:01', 'a.4', 'processing', 'D')"); // id 4, oldest

        var first = (await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, ActivityFilter.None, limit: 2, beforeId: null))).Items;
        Assert.Equal(["C", "A"], first.Select(r => r.Title)); // the tie broken by id DESC: 3 before 1

        var second = (await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, ActivityFilter.None, limit: 2, beforeId: first[^1].Id))).Items;
        Assert.Equal(["B", "D"], second.Select(r => r.Title)); // never id 1 again, never loses B or D

        Assert.Equal(4, first.Concat(second).Select(r => r.Id).Distinct().Count());
    }

    /// <summary>A cursor row that is gone (deleted since the page it came from was read) still pages, by id alone.</summary>
    [Fact]
    public async Task List_recent_before_a_missing_cursor_row_falls_back_to_id_only_paging()
    {
        using var fixture = new StoreFixture();
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title) VALUES " +
            "('2026-01-01 00:00:01', 'a.1', 'processing', 'one'), " +
            "('2026-01-01 00:00:02', 'a.2', 'processing', 'two')");

        var rows = (await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, ActivityFilter.None, limit: 10, beforeId: 999))).Items;
        Assert.Equal(["two", "one"], rows.Select(r => r.Title));
    }

    /// <summary>
    /// #543 item 2: a query offset must be honored (not silently taken as the naive wall clock), and a stored
    /// row without a fractional part must not be excluded from a boundary that names its exact second.
    /// </summary>
    [Fact]
    public async Task Date_filters_normalize_to_utc_and_compare_stored_shapes_not_raw_text()
    {
        using var fixture = new StoreFixture();
        // Stored with no fractional part: the shape Weir, and databases from earlier releases, hold for an exact second.
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title) VALUES ('2026-01-02 03:04:05', 'a.1', 'processing', 'exact')");

        Assert.True(Timestamp.TryFromIsoFormat("2026-01-02T03:04:05", out var naiveBoundary));
        var atBoundary = new ActivityFilter(DateFrom: naiveBoundary);
        Assert.Single((await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, atBoundary, limit: 10, beforeId: null))).Items);

        // The same instant, named with a +02:00 offset: an ignored offset would compare "05:04:05" against
        // the stored "03:04:05" and wrongly exclude the row.
        Assert.True(Timestamp.TryFromIsoFormat("2026-01-02T05:04:05+02:00", out var sameInstantOffset));
        var atOffsetBoundary = new ActivityFilter(DateFrom: sameInstantOffset);
        Assert.Single((await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, atOffsetBoundary, limit: 10, beforeId: null))).Items);

        // One second later in the same offset: now past the row, in either timezone.
        Assert.True(Timestamp.TryFromIsoFormat("2026-01-02T06:04:06+02:00", out var pastIt));
        var pastFilter = new ActivityFilter(DateFrom: pastIt);
        Assert.Empty((await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, pastFilter, limit: 10, beforeId: null))).Items);
    }

    /// <summary>
    /// The date filters narrow by the stored text before julianday() checks the instant (#714). A time written with a
    /// UTC offset can carry a different date from its UTC date, and must still be found from either side.
    /// </summary>
    [Fact]
    public async Task Date_filters_find_a_time_stored_with_an_offset_whose_written_date_is_another_day()
    {
        using var fixture = new StoreFixture();
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title) VALUES " +
            "('2026-01-02 23:30:00-05:00', 'a.1', 'processing', 'written the day before'), " + // 2026-01-03 04:30 UTC
            "('2026-01-03 03:00:00+10:00', 'a.2', 'processing', 'written the day after')"); // 2026-01-02 17:00 UTC

        Assert.True(Timestamp.TryFromIsoFormat("2026-01-03T04:00:00+00:00", out var from));
        Assert.True(Timestamp.TryFromIsoFormat("2026-01-02T18:00:00+00:00", out var to));
        var after = await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, new ActivityFilter(DateFrom: from), limit: 10, beforeId: null));
        var before = await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, new ActivityFilter(DateTo: to), limit: 10, beforeId: null));

        Assert.Equal(["written the day before"], after.Items.Select(row => row.Title));
        Assert.Equal(["written the day after"], before.Items.Select(row => row.Title));
    }

    [Fact]
    public async Task A_page_knows_whether_older_events_remain()
    {
        using var fixture = new StoreFixture();
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title) VALUES " +
            "('2026-01-01 00:00:01', 'a.1', 'processing', 'one'), " +
            "('2026-01-01 00:00:02', 'a.2', 'processing', 'two'), " +
            "('2026-01-01 00:00:03', 'a.3', 'processing', 'three')");

        var first = await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, ActivityFilter.None, limit: 2, beforeId: null));
        var second = await fixture.WithUnitOfWork(uow => _history.ListRecentAsync(uow, ActivityFilter.None, limit: 2, beforeId: first.Items[^1].Id));

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal(["one"], second.Items.Select(row => row.Title));
        Assert.False(second.HasMore);
    }

    /// <summary>
    /// #543 item 4: the Activity events filter matches a row that never recorded a library id even when one
    /// is given; processing records get the same fallback, so removing one file's history with a library id
    /// cleans up both consistently instead of leaving old records behind.
    /// </summary>
    [Fact]
    public async Task File_history_removal_gives_processing_records_the_same_library_null_fallback_as_events()
    {
        using var fixture = new StoreFixture();
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title, relative_path, library_id) VALUES " +
            "('2026-01-01 00:00:01', 'a.1', 'processing', 'no library recorded', 'Film/movie.mkv', NULL)");
        await fixture.Execute(
            "INSERT INTO file_logs (relative_path, recorded_at, library_id) VALUES ('Film/movie.mkv', '2026-01-01 00:00:01', NULL)");

        var counts = await fixture.WithUnitOfWork(uow => _history.CountFileHistoryAsync(uow, libraryId: 7, "Film/movie.mkv"));
        Assert.Equal(1, counts.ActivityEvents);
        Assert.Equal(1, counts.ProcessingRecords);

        var deleted = await fixture.WithUnitOfWork(uow => _history.DeleteFileHistoryAsync(uow, libraryId: 7, "Film/movie.mkv"));
        Assert.Equal(1, deleted.ActivityEvents);
        Assert.Equal(1, deleted.ProcessingRecords);
    }

    [Fact]
    public async Task File_history_asked_for_without_a_library_covers_that_path_in_every_library()
    {
        using var fixture = new StoreFixture();
        var first = await fixture.Scalar("INSERT INTO libraries (name, media_type) VALUES ('One', 'movie') RETURNING id");
        var second = await fixture.Scalar("INSERT INTO libraries (name, media_type) VALUES ('Two', 'movie') RETURNING id");
        await fixture.Execute(
            "INSERT INTO activity_events (created_at, event_type, module, title, relative_path, library_id) VALUES " +
            $"('2026-01-01 00:00:01', 'a.1', 'processing', 'in one library', 'Film/movie.mkv', {first}), " +
            $"('2026-01-01 00:00:02', 'a.1', 'processing', 'in another', 'Film/movie.mkv', {second})");
        await fixture.Execute(
            $"INSERT INTO file_logs (relative_path, recorded_at, library_id) VALUES ('Film/movie.mkv', '2026-01-01 00:00:01', {first}), ('Film/movie.mkv', '2026-01-01 00:00:02', {second})");

        var counts = await fixture.WithUnitOfWork(uow => _history.CountFileHistoryAsync(uow, libraryId: null, "Film/movie.mkv"));
        var deleted = await fixture.WithUnitOfWork(uow => _history.DeleteFileHistoryAsync(uow, libraryId: null, "Film/movie.mkv"));

        Assert.Equal((2, 2), (counts.ActivityEvents, counts.ProcessingRecords));
        Assert.Equal((2, 2), (deleted.ActivityEvents, deleted.ProcessingRecords));
    }
}
