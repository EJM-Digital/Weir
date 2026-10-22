using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// Saving changes to a download client connection: each field is written only when it changed, a blank secret clears it, and an address
/// that will not work is refused before anything is saved.
/// </summary>
public sealed class DownloadClientConnectionServiceUpdateTests
{
    private static async Task<DownloadClientConnectionRecord> ReadAsync(DownloadClientFixture fixture, long id) =>
        (await fixture.Db(uow => fixture.ConnectionStore.GetAsync(uow, id)))!;

    private static async Task UpdateAsync(
        DownloadClientFixture fixture,
        DownloadClientConnectionRecord row,
        string? baseUrl = null,
        string? username = null,
        string? password = null,
        string? apiKey = null,
        bool? enabled = null,
        string? nickname = null) =>
        await fixture.Db(
            async uow =>
            {
                await fixture.Connections.UpdateAsync(uow, row, baseUrl, username, password, apiKey, enabled, nickname);
                return 0;
            });

    [Fact]
    public async Task A_changed_address_username_password_and_nickname_are_all_saved()
    {
        using var fixture = new DownloadClientFixture();
        var id = await fixture.AddConnectionAsync("qbittorrent", username: "old", password: "old-secret");

        await UpdateAsync(fixture, await ReadAsync(fixture, id), baseUrl: "http://other.local:8080/", username: "  new  ", password: "new-secret", nickname: "  Seedbox  ");

        var row = await ReadAsync(fixture, id);
        Assert.Equal("http://other.local:8080", row.BaseUrl);
        Assert.Equal("new", row.Username);
        Assert.Equal("Seedbox", row.Nickname);
        Assert.Equal("new-secret", fixture.Connections.ConnectionFromRow(row)!.Password);
    }

    [Fact]
    public async Task Asking_for_what_is_already_saved_changes_nothing()
    {
        using var fixture = new DownloadClientFixture();
        var id = await fixture.AddConnectionAsync("qbittorrent", "http://client.local", username: "same");
        var before = await ReadAsync(fixture, id);

        await UpdateAsync(fixture, before, baseUrl: "http://client.local/", username: "same", password: "", apiKey: "  ", enabled: true, nickname: "");

        Assert.Equal(before, await ReadAsync(fixture, id));
    }

    [Fact]
    public async Task A_new_api_key_replaces_the_saved_one()
    {
        using var fixture = new DownloadClientFixture();
        var id = await fixture.AddConnectionAsync("sabnzbd", apiKey: "first-key");

        await UpdateAsync(fixture, await ReadAsync(fixture, id), apiKey: "second-key");

        Assert.Equal("second-key", fixture.Connections.ConnectionFromRow(await ReadAsync(fixture, id))!.ApiKey);
    }

    [Fact]
    public async Task A_blank_username_nickname_and_password_clear_what_was_saved()
    {
        using var fixture = new DownloadClientFixture();
        var id = await fixture.AddConnectionAsync("qbittorrent", username: "someone", password: "secret");
        await UpdateAsync(fixture, await ReadAsync(fixture, id), nickname: "Seedbox");

        await UpdateAsync(fixture, await ReadAsync(fixture, id), username: "   ", password: "", nickname: "  ");

        var row = await ReadAsync(fixture, id);
        Assert.Null(row.Username);
        Assert.Null(row.PasswordCiphertext);
        Assert.Null(row.Nickname);
    }

    [Fact]
    public async Task A_connection_is_switched_off_and_on_again()
    {
        using var fixture = new DownloadClientFixture();
        var id = await fixture.AddConnectionAsync("transmission");

        await UpdateAsync(fixture, await ReadAsync(fixture, id), enabled: false);
        Assert.False((await ReadAsync(fixture, id)).Enabled);

        await UpdateAsync(fixture, await ReadAsync(fixture, id), enabled: true);
        Assert.True((await ReadAsync(fixture, id)).Enabled);
    }

    [Theory]
    [InlineData("ftp://client.local")]
    [InlineData("not an address")]
    [InlineData("http://user:pass@client.local")]
    [InlineData("http://client.local/?token=1")]
    public async Task An_address_that_will_not_work_is_refused_and_nothing_is_saved(string address)
    {
        using var fixture = new DownloadClientFixture();
        var id = await fixture.AddConnectionAsync("transmission", "http://client.local");

        var thrown = await Assert.ThrowsAsync<DownloadClientConnectionException>(
            async () => await UpdateAsync(fixture, await ReadAsync(fixture, id), baseUrl: address, nickname: "Changed"));

        Assert.StartsWith("That address will not work: ", thrown.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<Exception>(thrown.InnerException);
        var row = await ReadAsync(fixture, id);
        Assert.Equal("http://client.local", row.BaseUrl);
        Assert.Null(row.Nickname);
    }

    [Fact]
    public async Task A_connection_with_no_address_cannot_be_switched_on_until_it_has_one()
    {
        using var fixture = new DownloadClientFixture();
        var id = await fixture.AddConnectionAsync("transmission", baseUrl: "", enabled: false);

        await UpdateAsync(fixture, await ReadAsync(fixture, id), baseUrl: "http://client.local", enabled: true);

        var row = await ReadAsync(fixture, id);
        Assert.True(row.Enabled);
        Assert.Equal("http://client.local", row.BaseUrl);
    }

    [Fact]
    public async Task A_connection_with_no_address_has_nothing_to_connect_to()
    {
        using var fixture = new DownloadClientFixture();
        var id = await fixture.AddConnectionAsync("transmission", baseUrl: "");

        Assert.Null(fixture.Connections.ConnectionFromRow(await ReadAsync(fixture, id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("rtorrent")]
    [InlineData("SAB")]
    public async Task A_kind_that_is_not_a_known_client_is_refused_with_the_list_of_known_ones(string kind)
    {
        using var fixture = new DownloadClientFixture();

        var thrown = await Assert.ThrowsAsync<DownloadClientConnectionException>(() => fixture.AddConnectionAsync(kind));

        Assert.StartsWith("Unknown download client kind ", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("sabnzbd", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_written_in_capitals_with_spaces_is_saved_in_its_plain_form()
    {
        using var fixture = new DownloadClientFixture();

        var id = await fixture.AddConnectionAsync("  Transmission ");

        Assert.Equal("transmission", (await ReadAsync(fixture, id)).Kind);
    }

    [Fact]
    public void The_service_hands_out_the_cipher_it_encrypts_with()
    {
        using var fixture = new DownloadClientFixture();

        Assert.Same(fixture.Cipher, fixture.Connections.Cipher);
    }

    [Fact]
    public void The_service_needs_both_a_cipher_and_a_store()
    {
        using var fixture = new DownloadClientFixture();

        Assert.Throws<ArgumentNullException>(() => new DownloadClientConnectionService(null!, fixture.ConnectionStore));
        Assert.Throws<ArgumentNullException>(() => new DownloadClientConnectionService(fixture.Cipher, null!));
    }

    [Fact]
    public void The_connection_exception_carries_a_message_and_the_error_that_caused_it()
    {
        var cause = new InvalidOperationException("the cause");

        Assert.Equal("a message", new DownloadClientConnectionException("a message").Message);
        Assert.Same(cause, new DownloadClientConnectionException("a message", cause).InnerException);
        Assert.NotNull(new DownloadClientConnectionException().Message);
    }
}
