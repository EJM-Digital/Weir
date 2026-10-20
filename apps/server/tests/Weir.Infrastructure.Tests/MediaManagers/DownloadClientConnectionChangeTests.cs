using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// Saving a download client connection by hand: what is refused and why, what an update changes and what it leaves alone, and
/// that a secret the person clears is cleared.
/// </summary>
public sealed class DownloadClientConnectionChangeTests : IDisposable
{
    private readonly DownloadClientFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private Task<DownloadClientConnectionRecord> RowAsync(long id) =>
        _fixture.Db(async uow => (await _fixture.ConnectionStore.GetAsync(uow, id))!);

    private async Task UpdateAsync(
        long id, string? baseUrl = null, string? username = null, string? password = null, string? apiKey = null, bool? enabled = null, string? nickname = null)
    {
        var row = await RowAsync(id);
        await _fixture.Db(async uow =>
        {
            await _fixture.Connections.UpdateAsync(uow, row, baseUrl, username, password, apiKey, enabled, nickname);
            return true;
        });
    }

    [Fact]
    public void The_exception_carries_its_message_and_its_cause()
    {
        var cause = new InvalidOperationException("cause");

        Assert.Equal("reason", new DownloadClientConnectionException("reason").Message);
        Assert.Same(cause, new DownloadClientConnectionException("reason", cause).InnerException);
        Assert.NotNull(new DownloadClientConnectionException().Message);
    }

    [Fact]
    public void The_service_exposes_the_cipher_it_encrypts_with()
    {
        Assert.Same(_fixture.Cipher, _fixture.Connections.Cipher);
    }

    [Fact]
    public async Task A_kind_Weir_does_not_know_is_refused_with_the_kinds_it_does()
    {
        var thrown = await Assert.ThrowsAsync<DownloadClientConnectionException>(() => _fixture.AddConnectionAsync("plex"));

        Assert.Contains("Unknown download client kind 'plex'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("qbittorrent", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_Weir_will_not_use_is_refused_with_the_reason()
    {
        var thrown = await Assert.ThrowsAsync<DownloadClientConnectionException>(() => _fixture.AddConnectionAsync("qbittorrent", "ftp://client.local"));

        Assert.StartsWith("That address will not work:", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_saved_with_no_address_has_nothing_to_call()
    {
        var id = await _fixture.AddConnectionAsync("qbittorrent", baseUrl: "  ");

        var row = await RowAsync(id);

        Assert.Equal(string.Empty, row.BaseUrl);
        Assert.Null(_fixture.Connections.ConnectionFromRow(row));
    }

    [Fact]
    public async Task A_kind_is_saved_in_lower_case_and_a_username_without_its_padding()
    {
        var id = await _fixture.AddConnectionAsync(" QBittorrent ", username: "  admin  ");

        var row = await RowAsync(id);

        Assert.Equal("qbittorrent", row.Kind);
        Assert.Equal("admin", row.Username);
    }

    [Fact]
    public async Task An_update_changes_every_field_it_is_given()
    {
        var id = await _fixture.AddConnectionAsync("qbittorrent", username: "old", password: "old-password", enabled: false);

        await UpdateAsync(id, "http://other.local:9090/", "new", "new-password", "new-key", enabled: true, nickname: "Torrents");

        var row = await RowAsync(id);
        var resolved = _fixture.Connections.ConnectionFromRow(row)!;
        Assert.Equal("http://other.local:9090", row.BaseUrl);
        Assert.True(row.Enabled);
        Assert.Equal("Torrents", row.Nickname);
        Assert.Equal("new", resolved.Username);
        Assert.Equal("new-password", resolved.Password);
        Assert.Equal("new-key", resolved.ApiKey);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_leaves_the_row_as_it_was()
    {
        var id = await _fixture.AddConnectionAsync("qbittorrent", username: "admin", enabled: true);
        await _fixture.Store.Execute("UPDATE download_client_connections SET updated_at = '2000-01-01 00:00:00'");
        var before = await RowAsync(id);

        await UpdateAsync(id, before.BaseUrl, "admin", nickname: before.Nickname ?? string.Empty, enabled: true);

        var after = await RowAsync(id);
        Assert.Equal(before.BaseUrl, after.BaseUrl);
        Assert.Equal(before.Username, after.Username);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM download_client_connections WHERE updated_at LIKE '2000-01-01%'"));
    }

    [Fact]
    public async Task An_update_with_an_empty_secret_clears_it_and_so_does_a_blank_username_or_nickname()
    {
        var id = await _fixture.AddConnectionAsync("qbittorrent", username: "admin", password: "secret", apiKey: "key");
        await UpdateAsync(id, nickname: "Torrents");

        await UpdateAsync(id, username: " ", password: "", apiKey: "", nickname: " ");

        var row = await RowAsync(id);
        Assert.Null(row.Username);
        Assert.Null(row.PasswordCiphertext);
        Assert.Null(row.ApiKeyCiphertext);
        Assert.Null(row.Nickname);
    }

    [Fact]
    public async Task An_update_that_leaves_every_secret_out_keeps_the_ones_saved()
    {
        var id = await _fixture.AddConnectionAsync("sabnzbd", apiKey: "saved-key");

        await UpdateAsync(id, nickname: "Usenet");

        Assert.Equal("saved-key", _fixture.Connections.ConnectionFromRow(await RowAsync(id))!.ApiKey);
    }

    [Fact]
    public async Task Clearing_the_api_key_sabnzbd_needs_is_refused_but_changing_only_its_password_is_not()
    {
        var id = await _fixture.AddConnectionAsync("sabnzbd", apiKey: "saved-key");

        var thrown = await Assert.ThrowsAsync<DownloadClientConnectionException>(() => UpdateAsync(id, apiKey: ""));
        await UpdateAsync(id, password: "extra");

        Assert.Equal("SABnzbd needs an API key. Find it in SABnzbd under Config → General.", thrown.Message);
        Assert.Equal("saved-key", _fixture.Connections.ConnectionFromRow(await RowAsync(id))!.ApiKey);
    }

    [Fact]
    public async Task Clearing_the_password_deluge_needs_is_refused()
    {
        var id = await _fixture.AddConnectionAsync("deluge", password: "saved-password");

        var thrown = await Assert.ThrowsAsync<DownloadClientConnectionException>(() => UpdateAsync(id, password: " "));

        Assert.Equal("Deluge needs its Web UI password.", thrown.Message);
    }

    [Fact]
    public async Task Switching_a_connection_on_checks_the_address_it_already_has()
    {
        var id = await _fixture.AddConnectionAsync("qbittorrent", enabled: false);
        await _fixture.Store.Execute("UPDATE download_client_connections SET base_url = 'ftp://restored.local'");

        var thrown = await Assert.ThrowsAsync<DownloadClientConnectionException>(() => UpdateAsync(id, enabled: true));

        Assert.StartsWith("That address will not work:", thrown.Message, StringComparison.Ordinal);
        Assert.False((await RowAsync(id)).Enabled);
    }

    [Fact]
    public async Task Switching_a_connection_on_and_giving_it_a_new_address_checks_the_new_one()
    {
        var id = await _fixture.AddConnectionAsync("qbittorrent", enabled: false);
        await _fixture.Store.Execute("UPDATE download_client_connections SET base_url = 'ftp://restored.local'");

        await UpdateAsync(id, baseUrl: "http://fixed.local", enabled: true);

        var row = await RowAsync(id);
        Assert.True(row.Enabled);
        Assert.Equal("http://fixed.local", row.BaseUrl);
    }

    [Fact]
    public async Task A_missing_argument_is_refused()
    {
        var id = await _fixture.AddConnectionAsync("qbittorrent");
        var row = await RowAsync(id);

        await _fixture.Db(async uow =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => _fixture.Connections.UpdateAsync(uow, null!));
            Assert.Throws<ArgumentNullException>(() => _fixture.Connections.ConnectionFromRow(null!));
            Assert.Throws<ArgumentNullException>(() => new DownloadClientConnectionService(null!, _fixture.ConnectionStore));
            Assert.Throws<ArgumentNullException>(() => new DownloadClientConnectionService(_fixture.Cipher, null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => _fixture.Connections.UpdateAsync(null!, row));
            await Assert.ThrowsAsync<ArgumentNullException>(() => _fixture.Connections.CreateAsync(null!, "qbittorrent"));
            return true;
        });
    }
}
