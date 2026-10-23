using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>Saving a connection that is not right, reading one that is half set up, and asking managers whose kind Weir has no dialect for.</summary>
public sealed class MediaManagerConnectionServiceEdgeTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<MediaManagerConnectionRecord> RowAsync(long id) =>
        (await _fixture.Db(uow => _fixture.ConnectionStore.GetAsync(uow, id)))!;

    private sealed class NoPorts : IMediaManagerPorts
    {
        public IMediaManagerPort? PortForKind(string? kind) => null;
    }

    private MediaManagerConnectionService ServiceWithoutPorts() =>
        new(_fixture.Store.Options, _fixture.Cipher, new NoPorts(), _fixture.ConnectionStore);

    [Fact]
    public void A_connection_error_can_be_made_with_or_without_its_cause()
    {
        var cause = new InvalidOperationException("inner");

        Assert.NotNull(new MediaManagerConnectionException().Message);
        Assert.Equal("plain", new MediaManagerConnectionException("plain").Message);
        Assert.Same(cause, new MediaManagerConnectionException("wrapped", cause).InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plex")]
    [InlineData("  ")]
    public async Task A_kind_weir_does_not_know_cannot_be_saved(string kind)
    {
        var failure = await Assert.ThrowsAsync<MediaManagerConnectionException>(() => _fixture.AddConnectionAsync(kind));

        Assert.StartsWith("Unknown media manager kind", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_with_no_kind_at_all_cannot_be_saved()
    {
        var failure = await Assert.ThrowsAsync<MediaManagerConnectionException>(() => _fixture.Db(uow => _fixture.Connections.CreateAsync(uow, null!)));

        Assert.StartsWith("Unknown media manager kind", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_address_at_all_is_an_empty_address()
    {
        Assert.Equal(string.Empty, MediaManagerConnectionService.ValidateBaseUrl(null));
    }

    [Fact]
    public async Task A_switched_off_connection_with_a_working_address_can_be_switched_on()
    {
        var id = await _fixture.AddConnectionAsync("sonarr", "http://manager.local", enabled: false);
        var row = await RowAsync(id);

        await _fixture.Db(async uow =>
        {
            await _fixture.Connections.UpdateAsync(uow, row, enabled: true);
            return 0;
        });

        Assert.True((await RowAsync(id)).Enabled);
    }

    [Fact]
    public async Task An_address_that_will_not_work_cannot_be_saved()
    {
        var failure = await Assert.ThrowsAsync<MediaManagerConnectionException>(() => _fixture.AddConnectionAsync("sonarr", "ftp://manager.local"));

        Assert.StartsWith("That address will not work", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_the_downloaded_scan_on_and_off_is_remembered()
    {
        var id = await _fixture.AddConnectionAsync("sonarr");

        await _fixture.Db(async uow =>
        {
            await _fixture.Connections.UpdateAsync(uow, await RowAsync(id), downloadedScanEnabled: true);
            return 0;
        });
        Assert.True((await RowAsync(id)).DownloadedScanEnabled);

        await _fixture.Db(async uow =>
        {
            await _fixture.Connections.UpdateAsync(uow, await RowAsync(id), downloadedScanEnabled: true);
            await _fixture.Connections.UpdateAsync(uow, await RowAsync(id), downloadedScanEnabled: false);
            return 0;
        });
        Assert.False((await RowAsync(id)).DownloadedScanEnabled);
    }

    [Fact]
    public async Task A_webhook_secret_only_matches_what_it_was_made_from()
    {
        var id = await _fixture.AddConnectionAsync("sonarr");
        var secret = await _fixture.Db(async uow => await _fixture.Connections.RotateWebhookSecretAsync(uow, await RowAsync(id)));
        var row = await RowAsync(id);

        Assert.True(_fixture.Connections.WebhookSecretMatches(row, secret));
        Assert.False(_fixture.Connections.WebhookSecretMatches(row, "something else"));
        Assert.False(_fixture.Connections.WebhookSecretMatches(row, null));
        Assert.False(_fixture.Connections.WebhookSecretMatches(row, string.Empty));
    }

    [Fact]
    public async Task A_connection_with_no_secret_needs_none_and_one_with_an_unreadable_secret_matches_nothing()
    {
        var id = await _fixture.AddConnectionAsync("sonarr");
        var row = await RowAsync(id);
        Assert.True(_fixture.Connections.WebhookSecretMatches(row, null));

        var emptied = row with { WebhookSecretCiphertext = _fixture.Cipher.Encrypt(" ") };
        Assert.False(_fixture.Connections.WebhookSecretMatches(emptied, "anything"));
    }

    [Fact]
    public async Task A_connection_with_no_address_or_no_key_cannot_be_used_or_reported_to()
    {
        var id = await _fixture.AddConnectionAsync("sonarr");
        var row = await RowAsync(id);

        Assert.NotNull(_fixture.Connections.ConnectionFromRow(row));
        Assert.Null(_fixture.Connections.ConnectionFromRow(row with { BaseUrl = " " }));
        Assert.Null(_fixture.Connections.ConnectionFromRow(row with { ApiKeyCiphertext = null }));
        Assert.Null(_fixture.Connections.ConnectionFromRow(row with { ApiKeyCiphertext = _fixture.Cipher.Encrypt(" ") }));
        Assert.Null(_fixture.Connections.ResolveCallbackTarget(row with { BaseUrl = " " }));
    }

    [Fact]
    public async Task A_callback_target_without_a_key_has_no_key()
    {
        var id = await _fixture.AddConnectionAsync("sonarr", "http://manager.local/");
        var row = await RowAsync(id);

        var withKey = _fixture.Connections.ResolveCallbackTarget(row);
        var withoutKey = _fixture.Connections.ResolveCallbackTarget(row with { ApiKeyCiphertext = null });

        Assert.Equal("key", withKey!.ApiKey);
        Assert.Null(withoutKey!.ApiKey);
        Assert.Equal(withKey.BaseUrl, withoutKey.BaseUrl);
        Assert.False(withKey.BaseUrl.EndsWith('/'));
    }

    [Fact]
    public async Task Managers_of_a_kind_with_no_dialect_are_skipped_when_asking_what_they_know()
    {
        var id = await _fixture.AddConnectionAsync("sonarr");
        var service = ServiceWithoutPorts();

        var queues = await _fixture.Db(uow => service.CollectQueueSignalsAsync(uow, "tv", [id]));
        var truths = await _fixture.Db(uow => service.CollectLibraryTruthAsync(uow, "tv", [id]));
        var described = await _fixture.Db(uow => service.DescribeConnectionsAsync(uow));

        Assert.Empty(queues);
        Assert.Empty(truths);
        Assert.Empty(described);
    }

    [Fact]
    public async Task No_connection_ids_means_no_connections()
    {
        var id = await _fixture.AddConnectionAsync("sonarr");

        Assert.Empty(await _fixture.Db(uow => _fixture.Connections.ConnectionsByIdAsync(uow, [])));
        Assert.Single(await _fixture.Db(uow => _fixture.Connections.ConnectionsByIdAsync(uow, [id])));
    }
}
