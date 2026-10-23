using Weir.Core.MediaManagers;
using Weir.Core.Time;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Download client connections when a kind or an address is missing, when nothing keeps their usage, and when a saved one is of a kind this build has no port for.</summary>
public sealed class DownloadClientEdgeTests : IDisposable
{
    private readonly DownloadClientFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void A_kind_that_is_missing_or_unknown_has_no_port()
    {
        Assert.Null(_fixture.Ports.PortForKind(null));
        Assert.Null(_fixture.Ports.PortForKind("mystery"));
        Assert.Equal(DownloadClientKinds.Sabnzbd, _fixture.Ports.PortForKind(DownloadClientKinds.Sabnzbd)!.Kind);
    }

    [Fact]
    public async Task A_connection_with_no_kind_is_refused_by_name()
    {
        var refused = await Assert.ThrowsAsync<DownloadClientConnectionException>(
            () => _fixture.Db(uow => _fixture.Connections.CreateAsync(uow, null!, "http://client.local", apiKey: "k")));

        Assert.StartsWith("Unknown download client kind ''.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_with_no_address_is_saved_without_one()
    {
        var id = await _fixture.Db(uow => _fixture.Connections.CreateAsync(uow, DownloadClientKinds.Sabnzbd, null!, apiKey: "k"));

        var row = await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, id), commit: false);
        Assert.Equal(string.Empty, row!.BaseUrl);
    }

    [Fact]
    public async Task A_removed_connection_is_forgotten_by_the_usage_ledger_when_there_is_one_and_removed_all_the_same_when_there_is_not()
    {
        var withLedger = new DownloadClientConnectionStore(new ConnectionUsageLedger());
        var first = await _fixture.AddConnectionAsync(DownloadClientKinds.Sabnzbd, apiKey: "k");
        var second = await _fixture.AddConnectionAsync(DownloadClientKinds.Nzbget);

        await _fixture.Db(async uow => { await withLedger.DeleteAsync(uow, first); return 0; });
        await _fixture.Db(async uow => { await _fixture.ConnectionStore.DeleteAsync(uow, second); return 0; });

        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM download_client_connections"));
    }

    [Fact]
    public async Task Usage_that_carries_only_part_of_what_is_known_leaves_the_rest_as_it_was()
    {
        var id = await _fixture.AddConnectionAsync(DownloadClientKinds.Nzbget);

        await _fixture.Db(uow => _fixture.ConnectionStore.RecordUsageAsync(uow, id, new ConnectionUsage(42, null)));
        await _fixture.Db(uow => _fixture.ConnectionStore.RecordUsageAsync(uow, id, new ConnectionUsage(null, Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow))));

        Assert.Equal(42, await _fixture.Store.Scalar($"SELECT last_answer_ms FROM download_client_connections WHERE id = {id}"));
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT last_used_at IS NOT NULL FROM download_client_connections WHERE id = {id}"));
    }

    [Fact]
    public async Task A_saved_connection_of_a_kind_this_build_has_no_port_for_is_left_out_of_the_suggestions()
    {
        var id = await _fixture.AddConnectionAsync(DownloadClientKinds.Sabnzbd, apiKey: "k");
        await _fixture.Store.Execute($"UPDATE download_client_connections SET kind = 'mystery' WHERE id = {id}");

        var suggestions = await _fixture.Db(uow => _fixture.Suggestions.SuggestAsync(uow), commit: false);

        Assert.Empty(suggestions);
        Assert.Empty(_fixture.Http.Requests);
    }

    [Fact]
    public async Task A_saved_connection_with_no_address_is_left_out_of_the_suggestions()
    {
        var id = await _fixture.AddConnectionAsync(DownloadClientKinds.Sabnzbd, apiKey: "k");
        await _fixture.Store.Execute($"UPDATE download_client_connections SET base_url = '' WHERE id = {id}");

        var suggestions = await _fixture.Db(uow => _fixture.Suggestions.SuggestAsync(uow), commit: false);

        Assert.Empty(suggestions);
        Assert.Empty(_fixture.Http.Requests);
    }
}
