using Weir.Core.MediaManagers;
using Weir.Core.Time;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.ConnectionTraffic;

/// <summary>How long a connection took and when it was last used: kept in memory, saved on a timer, and read back newest first.</summary>
public sealed class ConnectionUsageTests
{
    private static readonly ConnectionRef Radarr = new(ConnectionKind.MediaManager, 1);
    private static readonly Timestamp Noon = Timestamp.FromUtc(new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));
    private static readonly Timestamp OneSecondLater = Timestamp.FromUtc(new DateTime(2026, 10, 2, 12, 0, 1, DateTimeKind.Utc));

    private static ConnectionActivity Activity(ConnectionPhase phase, ConnectionDirection direction, Timestamp at, long? milliseconds) =>
        new(Radarr, phase, direction, at, milliseconds);

    [Fact]
    public void A_finished_call_records_how_long_it_took_and_when()
    {
        var ledger = new ConnectionUsageLedger();

        ledger.Record(Activity(ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));

        Assert.Equal(new ConnectionUsage(84, Noon), ledger.Overlay(Radarr, null, null));
    }

    [Fact]
    public void A_failed_call_records_how_long_it_took_too()
    {
        var ledger = new ConnectionUsageLedger();

        ledger.Record(Activity(ConnectionPhase.Failed, ConnectionDirection.Outbound, Noon, 10_000));

        Assert.Equal(new ConnectionUsage(10_000, Noon), ledger.Overlay(Radarr, null, null));
    }

    [Fact]
    public void A_call_still_being_asked_records_nothing()
    {
        var ledger = new ConnectionUsageLedger();

        ledger.Record(Activity(ConnectionPhase.Asked, ConnectionDirection.Outbound, Noon, null));

        Assert.Equal(new ConnectionUsage(null, null), ledger.Overlay(Radarr, null, null));
        Assert.Empty(ledger.TakeUnsaved());
    }

    [Fact]
    public void A_call_from_the_connection_updates_when_it_was_used_and_keeps_how_long_the_last_call_took()
    {
        var ledger = new ConnectionUsageLedger();
        ledger.Record(Activity(ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));

        ledger.Record(Activity(ConnectionPhase.Answered, ConnectionDirection.Inbound, OneSecondLater, null));

        Assert.Equal(new ConnectionUsage(84, OneSecondLater), ledger.Overlay(Radarr, null, null));
    }

    [Fact]
    public void What_is_stored_shows_through_until_something_newer_is_recorded()
    {
        var ledger = new ConnectionUsageLedger();

        Assert.Equal(new ConnectionUsage(200, Noon), ledger.Overlay(Radarr, 200, Noon));

        ledger.Record(Activity(ConnectionPhase.Answered, ConnectionDirection.Outbound, OneSecondLater, 90));

        Assert.Equal(new ConnectionUsage(90, OneSecondLater), ledger.Overlay(Radarr, 200, Noon));
    }

    [Fact]
    public void Many_calls_between_two_saves_leave_one_change_to_save_with_the_newest_values()
    {
        var ledger = new ConnectionUsageLedger();
        ledger.Record(Activity(ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));
        ledger.Record(Activity(ConnectionPhase.Answered, ConnectionDirection.Outbound, OneSecondLater, 91));

        var unsaved = ledger.TakeUnsaved();

        Assert.Equal([(Radarr, new ConnectionUsage(91, OneSecondLater))], unsaved);
        Assert.Empty(ledger.TakeUnsaved());
    }

    [Fact]
    public void A_save_that_failed_is_tried_again_with_the_same_values()
    {
        var ledger = new ConnectionUsageLedger();
        ledger.Record(Activity(ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));
        var unsaved = ledger.TakeUnsaved();

        ledger.ReturnUnsaved(unsaved.Select(entry => entry.Connection));

        Assert.Equal(unsaved, ledger.TakeUnsaved());
    }

    [Fact]
    public void A_removed_connection_is_forgotten_so_a_new_one_with_its_id_starts_clean()
    {
        var ledger = new ConnectionUsageLedger();
        ledger.Record(Activity(ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));

        ledger.Forget(Radarr);

        Assert.Equal(new ConnectionUsage(null, null), ledger.Overlay(Radarr, null, null));
        Assert.Empty(ledger.TakeUnsaved());
    }

    [Fact]
    public async Task The_flush_task_saves_what_changed_and_a_list_of_connections_reads_it_back()
    {
        using var fixture = new MediaManagerFixture();
        var id = await fixture.AddConnectionAsync("radarr");
        var connection = new ConnectionRef(ConnectionKind.MediaManager, id);
        fixture.Usage.Record(new ConnectionActivity(connection, ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));
        var clients = new DownloadClientConnectionStore();
        var flush = new ConnectionUsageFlushTask(fixture.Store.Database, fixture.Usage, fixture.ConnectionStore, clients);

        await flush.RunOnceAsync(CancellationToken.None);

        var stored = await fixture.Db(uow => new MediaManagerConnectionStore().GetAsync(uow, id));
        Assert.Equal(84, stored!.LastAnswerMs);
        Assert.Equal(Noon.AsUtc, stored.LastUsedAt!.Value.AsUtc);
        Assert.Empty(fixture.Usage.TakeUnsaved());
    }

    [Fact]
    public async Task The_flush_task_leaves_an_unchanged_connection_alone()
    {
        using var fixture = new MediaManagerFixture();
        var id = await fixture.AddConnectionAsync("radarr");
        var flush = new ConnectionUsageFlushTask(fixture.Store.Database, fixture.Usage, fixture.ConnectionStore, new DownloadClientConnectionStore());

        await flush.RunOnceAsync(CancellationToken.None);

        var stored = await fixture.Db(uow => new MediaManagerConnectionStore().GetAsync(uow, id));
        Assert.Null(stored!.LastAnswerMs);
        Assert.Null(stored.LastUsedAt);
    }

    [Fact]
    public async Task A_connection_list_shows_newer_usage_than_the_database_holds()
    {
        using var fixture = new MediaManagerFixture();
        var id = await fixture.AddConnectionAsync("radarr");
        fixture.Usage.Record(new ConnectionActivity(
            new ConnectionRef(ConnectionKind.MediaManager, id), ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));

        var listed = Assert.Single(await fixture.Db(uow => fixture.ConnectionStore.ListAsync(uow)));

        Assert.Equal(84, listed.LastAnswerMs);
        Assert.Equal(Noon.AsUtc, listed.LastUsedAt!.Value.AsUtc);
    }

    [Fact]
    public async Task A_deleted_manager_connection_takes_its_usage_with_it()
    {
        using var fixture = new MediaManagerFixture();
        var id = await fixture.AddConnectionAsync("radarr");
        var connection = new ConnectionRef(ConnectionKind.MediaManager, id);
        fixture.Usage.Record(new ConnectionActivity(connection, ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 84));

        await fixture.Db(async uow =>
        {
            await fixture.ConnectionStore.DeleteAsync(uow, id);
            return 0;
        });

        Assert.Equal(new ConnectionUsage(null, null), fixture.Usage.Overlay(connection, null, null));
    }

    [Fact]
    public async Task A_download_client_connection_reads_its_usage_the_same_way()
    {
        using var fixture = new DownloadClientFixture();
        var usage = new ConnectionUsageLedger();
        var store = new DownloadClientConnectionStore(usage);
        var id = await fixture.AddConnectionAsync("qbittorrent");
        var connection = new ConnectionRef(ConnectionKind.DownloadClient, id);
        await fixture.Db(uow => store.RecordUsageAsync(uow, id, new ConnectionUsage(33, Noon)));
        usage.Record(new ConnectionActivity(connection, ConnectionPhase.Answered, ConnectionDirection.Outbound, OneSecondLater, 41));

        var stored = await fixture.Db(uow => new DownloadClientConnectionStore().GetAsync(uow, id));
        var live = await fixture.Db(uow => store.GetAsync(uow, id));

        Assert.Equal(33, stored!.LastAnswerMs);
        Assert.Equal(Noon.AsUtc, stored.LastUsedAt!.Value.AsUtc);
        Assert.Equal(41, live!.LastAnswerMs);
        Assert.Equal(OneSecondLater.AsUtc, live.LastUsedAt!.Value.AsUtc);
    }

    [Fact]
    public async Task The_flush_task_saves_a_download_clients_usage_to_the_download_client()
    {
        using var fixture = new DownloadClientFixture();
        var usage = new ConnectionUsageLedger();
        var id = await fixture.AddConnectionAsync("qbittorrent");
        usage.Record(new ConnectionActivity(new ConnectionRef(ConnectionKind.DownloadClient, id), ConnectionPhase.Answered, ConnectionDirection.Outbound, Noon, 52));
        var flush = new ConnectionUsageFlushTask(fixture.Store.Database, usage, new MediaManagerConnectionStore(), fixture.ConnectionStore);

        await flush.RunOnceAsync(CancellationToken.None);

        var stored = await fixture.Db(uow => new DownloadClientConnectionStore().GetAsync(uow, id));
        Assert.Equal(52, stored!.LastAnswerMs);
        Assert.Empty(usage.TakeUnsaved());
    }

    [Fact]
    public async Task A_connection_never_used_lists_both_values_as_null()
    {
        using var fixture = new MediaManagerFixture();
        var id = await fixture.AddConnectionAsync("radarr");

        var row = await fixture.Db(uow => new MediaManagerConnectionStore().GetAsync(uow, id));

        var output = row!.ToOut();
        Assert.Equal(Weir.Core.Json.WireNull.Instance, output["last_answer_ms"]);
        Assert.Equal(Weir.Core.Json.WireNull.Instance, output["last_used_at"]);
    }
}
