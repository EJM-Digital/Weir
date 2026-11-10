using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// The ledger store's own edges: what it records and reads back, which hand-offs have not ended, which belong to a connection,
/// and when a cancelled pass ends a hand-off. Each case sits where a one-step change to the code would give another answer.
/// </summary>
public sealed class HandoffLedgerStoreMutationTests : IDisposable
{
    private const string Pack = "Show.S01";

    private readonly MediaManagerFixture _fixture = new();
    private long _libraryId;

    public void Dispose() => _fixture.Dispose();

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);

    private async Task<long> ReceiveAsync(string source, string id, string path = Pack, long? connection = null, bool inLibrary = true)
    {
        if (inLibrary && _libraryId == 0)
        {
            _libraryId = await _fixture.LibraryAsync("tv", _fixture.Store.Home.Join("tv"));
        }

        return await _fixture.Db(uow => _fixture.Ledger.RecordReceivedAsync(uow, source, id, inLibrary ? _libraryId : null, path, connection));
    }

    private Task<HandoffLedgerRow?> FindAsync(string source, string id) => _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, source, id));

    private Task SetStateAsync(string id, string state) =>
        _fixture.Store.Execute($"UPDATE media_manager_handoffs SET state = '{state}' WHERE handoff_id = '{id}'");

    // ---- recording and reading ----

    [Fact]
    public async Task A_hand_off_in_no_library_reads_back_with_no_library()
    {
        await ReceiveAsync("radarr", "h-1", inLibrary: false);

        var row = await FindAsync("radarr", "h-1");

        Assert.Null(row!.LibraryId);
        Assert.Null(row.ConnectionId);
    }

    [Fact]
    public async Task A_hand_off_with_no_id_is_never_updated_by_a_result()
    {
        await ReceiveAsync("radarr", string.Empty, inLibrary: false);

        await _fixture.Db(async uow =>
        {
            await _fixture.Ledger.RecordOutcomeAsync(uow, "radarr", string.Empty, HandoffLedgerRules.Completed, "/out/film.mkv");
            await _fixture.Ledger.RecordOutcomeAsync(uow, "radarr", null, HandoffLedgerRules.Completed, "/out/film.mkv");
            return 0;
        });

        var row = await FindAsync("radarr", string.Empty);
        Assert.Equal(HandoffLedgerRules.Queued, row!.State);
        Assert.Null(row.OutputPath);
    }

    [Fact]
    public async Task A_report_is_owed_to_the_manager_only_for_the_hand_off_that_saved_one()
    {
        await ReceiveAsync("radarr", "h-1", inLibrary: false);
        await ReceiveAsync("radarr", "h-2", inLibrary: false);
        await _fixture.Db(async uow =>
        {
            await HandoffLedgerStore.SetPendingReportAsync(uow, "radarr", "h-2", "{\"status\":\"completed\"}");
            return 0;
        });

        var none = await _fixture.Db(uow => HandoffLedgerStore.PendingReportAsync(uow, "radarr", "h-1"));
        var unknown = await _fixture.Db(uow => HandoffLedgerStore.PendingReportAsync(uow, "radarr", "nobody"));
        var saved = await _fixture.Db(uow => HandoffLedgerStore.PendingReportAsync(uow, "radarr", "h-2"));

        Assert.Null(none);
        Assert.Null(unknown);
        Assert.Equal("{\"status\":\"completed\"}", saved);
    }

    [Fact]
    public async Task A_rejection_is_noted_for_a_hand_off_not_yet_reported_or_reported_failed_and_for_no_other()
    {
        foreach (var id in new[] { "new", "failed", "done" })
        {
            await ReceiveAsync("radarr", id, inLibrary: false);
        }

        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET reported_status = 'failed' WHERE handoff_id = 'failed'");
        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET reported_status = 'completed' WHERE handoff_id = 'done'");

        var counts = new List<int>();
        foreach (var id in new[] { "new", "failed", "done" })
        {
            counts.Add(await _fixture.Db(uow => HandoffLedgerStore.RecordRejectionReportedAsync(uow, "radarr", id)));
        }

        Assert.Equal([1, 1, 0], counts);
        Assert.Equal("rejected", (await FindAsync("radarr", "new"))!.ReportedStatus);
        Assert.Equal("rejected", (await FindAsync("radarr", "failed"))!.ReportedStatus);
        Assert.Equal("completed", (await FindAsync("radarr", "done"))!.ReportedStatus);
    }

    // ---- hand-offs that have not ended ----

    [Fact]
    public async Task Only_the_hand_offs_that_have_not_reached_a_final_state_are_unfinished()
    {
        var states = new Dictionary<string, string>
        {
            ["a"] = "queued", ["b"] = "completed", ["c"] = "failed", ["d"] = "working", ["e"] = "cancelled", ["f"] = "passed-through", ["g"] = "rejected",
        };
        foreach (var (id, state) in states)
        {
            await ReceiveAsync("radarr", id, inLibrary: false);
            await SetStateAsync(id, state);
        }

        var unfinished = await _fixture.Db(HandoffLedgerStore.UnfinishedAsync);

        Assert.Equal(["a", "d"], unfinished.Select(row => row.HandoffId));
    }

    // ---- the hand-offs of one connection ----

    private async Task<(MediaManagerConnectionRecord First, MediaManagerConnectionRecord Second)> TwoRadarrsAsync()
    {
        var first = await _fixture.AddConnectionAsync("radarr", "http://nas:7878");
        var second = await _fixture.AddConnectionAsync("radarr", "http://nas:7879");
        return (
            (await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, first)))!,
            (await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, second)))!);
    }

    private Task<(int InProgress, List<OwedReportRow> Owed)> OpenAsync(MediaManagerConnectionRecord connection, bool onlyOfItsKind) =>
        _fixture.Db(uow => _fixture.Ledger.OpenForConnectionAsync(uow, connection, onlyOfItsKind));

    [Fact]
    public async Task Every_hand_off_of_a_connection_still_being_worked_on_is_counted()
    {
        var (first, second) = await TwoRadarrsAsync();
        await ReceiveAsync("radarr", "h-1", connection: first.Id);
        await ReceiveAsync("radarr", "h-2", connection: first.Id);
        await ReceiveAsync("radarr", "h-3", connection: second.Id);

        var (inProgress, owed) = await OpenAsync(first, onlyOfItsKind: false);

        Assert.Equal(2, inProgress);
        Assert.Empty(owed);
    }

    [Fact]
    public async Task A_hand_off_that_ended_is_not_counted_even_when_work_for_it_is_running_again()
    {
        var (first, _) = await TwoRadarrsAsync();
        await ReceiveAsync("radarr", "ended", connection: first.Id);
        await SetStateAsync("ended", HandoffLedgerRules.Completed);
        await _fixture.Store.Execute(
            $"INSERT INTO jobs (dedupe_key, job_kind, payload_json, status) VALUES ('{IntakeRules.RemuxDedupeKey("radarr", "ended")}', '{IntakeRules.RemuxPassJobKind}', '{{}}', 'leased')");

        var (inProgress, _) = await OpenAsync(first, onlyOfItsKind: false);

        Assert.Equal(0, inProgress);
    }

    [Fact]
    public async Task A_hand_off_whose_files_are_all_done_is_not_counted_though_the_ledger_has_not_caught_up()
    {
        var (first, _) = await TwoRadarrsAsync();
        await ReceiveAsync("radarr", "done-now", path: Pack + "/Ep1.mkv", connection: first.Id);
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status) VALUES ({_libraryId}, '{Pack}/Ep1.mkv', 'processed')");

        var (inProgress, _) = await OpenAsync(first, onlyOfItsKind: false);

        Assert.Equal(0, inProgress);
    }

    [Fact]
    public async Task A_hand_off_nobody_could_attribute_belongs_to_the_connection_only_when_it_is_the_only_one_of_its_kind()
    {
        var (first, _) = await TwoRadarrsAsync();
        await ReceiveAsync("radarr", "unattributed");
        await ReceiveAsync("sonarr", "other-kind");
        await _fixture.Db(async uow =>
        {
            await HandoffLedgerStore.SetPendingReportAsync(uow, "radarr", "unattributed", "{}");
            await HandoffLedgerStore.SetPendingReportAsync(uow, "sonarr", "other-kind", "{}");
            return 0;
        });

        var sole = await OpenAsync(first, onlyOfItsKind: true);
        var shared = await OpenAsync(first, onlyOfItsKind: false);

        Assert.Equal(1, sole.InProgress);
        Assert.Equal(["unattributed"], sole.Owed.Select(owed => owed.HandoffId));
        Assert.Equal(0, shared.InProgress);
        Assert.Empty(shared.Owed);
    }

    [Fact]
    public async Task The_reports_owed_to_a_connection_are_its_own_oldest_first_with_what_they_were_about()
    {
        var (first, second) = await TwoRadarrsAsync();
        await ReceiveAsync("radarr", "h-1", path: "Film.One (2001)/", connection: first.Id);
        await ReceiveAsync("radarr", "h-2", path: "Film.Two.mkv", connection: first.Id);
        await ReceiveAsync("radarr", "h-3", connection: second.Id);
        await _fixture.Db(async uow =>
        {
            await HandoffLedgerStore.SetPendingReportAsync(uow, "radarr", "h-2", "{\"two\":true}");
            await HandoffLedgerStore.SetPendingReportAsync(uow, "radarr", "h-1", "{\"one\":true}");
            await HandoffLedgerStore.SetPendingReportAsync(uow, "radarr", "h-3", "{\"three\":true}");
            return 0;
        });

        var owed = await _fixture.Db(uow => _fixture.Ledger.OwedReportsAsync(uow, first, onlyOfItsKind: false));

        Assert.Equal(
            [("radarr", "h-1", "Film.One (2001)/", "{\"one\":true}"), ("radarr", "h-2", "Film.Two.mkv", "{\"two\":true}")],
            owed.Select(row => (row.SourceKey, row.HandoffId, row.RelativePath, row.ReportJson)));
        Assert.Equal("Film.One (2001)", owed[0].ToOwedReport().Name);
    }

    // ---- a cancelled pass ending the hand-off ----

    private Task<bool> SettleAsync(string id, DateTimeOffset handedOverAt) => _fixture.Db(async uow =>
        await _fixture.Ledger.SettleAfterJobCancelledAsync(uow, (await HandoffLedgerStore.FindAsync(uow, "sonarr", id))!, handedOverAt, "Cancelled in Weir."));

    private Task AddFileAsync(string name, string status, string updatedAt) =>
        _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, updated_at) VALUES ({_libraryId}, '{Pack}/{name}', '{status}', '{updatedAt}')");

    [Theory]
    [InlineData("processed")]
    [InlineData("passed_through")]
    [InlineData("rejected")]
    public async Task A_hand_off_with_a_file_delivered_since_it_was_handed_over_does_not_end_cancelled(string status)
    {
        await ReceiveAsync("sonarr", "h-1");
        await AddFileAsync("Ep1.mkv", status, "2026-01-15 10:30:00");

        Assert.False(await SettleAsync("h-1", At(10)));
        Assert.NotEqual(HandoffLedgerRules.Cancelled, (await FindAsync("sonarr", "h-1"))!.State);
    }

    [Fact]
    public async Task A_pack_with_one_file_delivered_and_another_never_started_does_not_end_cancelled()
    {
        await ReceiveAsync("sonarr", "h-1");
        await AddFileAsync("Ep1.mkv", "unprocessed", "2026-01-15 09:00:00");
        await AddFileAsync("Ep2.mkv", "processed", "2026-01-15 10:30:00");

        Assert.False(await SettleAsync("h-1", At(10)));
    }

    [Fact]
    public async Task A_file_delivered_at_the_moment_of_the_hand_over_counts_as_delivered_for_it_and_one_a_moment_before_does_not()
    {
        await ReceiveAsync("sonarr", "at-once");
        await AddFileAsync("Ep1.mkv", "processed", "2026-01-15 10:00:00");
        await ReceiveAsync("sonarr", "before", path: Pack + "-two");
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, updated_at) VALUES ({_libraryId}, '{Pack}-two/Ep1.mkv', 'processed', '2026-01-15 09:59:59')");

        Assert.False(await SettleAsync("at-once", At(10)));
        Assert.True(await SettleAsync("before", At(10)));
        var ended = (await FindAsync("sonarr", "before"))!;
        Assert.Equal(HandoffLedgerRules.Cancelled, ended.State);
        Assert.Equal("Cancelled in Weir.", ended.Message);
    }
}
