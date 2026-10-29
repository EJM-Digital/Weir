using Weir.Core.Configuration;
using Weir.Core.Net;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>
/// The saved choice to skip sign-in on the home network, and who it lets in: the account's own identity for one request, and
/// nobody when it is off, the client is not known to be local, or there is no account to be.
/// </summary>
public sealed class HomeNetworkSignInTests : IDisposable
{
    private readonly StoreFixture _store;
    private readonly HomeNetworkSignInStore _settings = new();

    public HomeNetworkSignInTests() => _store = new StoreFixture();

    public void Dispose() => _store.Dispose();

    private HomeNetworkSignIn SignIn(WeirOptions? options = null) => new(_settings, _store.Users, options ?? _store.Options, _store.Clock);

    private Task<int> SaveAsync(HomeNetworkSettings settings) =>
        _store.WithUnitOfWork(async uow =>
        {
            await _settings.SaveAsync(uow, settings);
            return 0;
        });

    private Task<long> AddAdminAsync(bool active = true) =>
        _store.WithUnitOfWork(uow => _store.Users.InsertUserAsync(uow, "alice", "hash", "admin", active));

    [Fact]
    public async Task Nothing_is_saved_until_the_owner_saves_it_and_that_means_off()
    {
        var read = await _store.WithUnitOfWork(uow => _settings.GetAsync(uow));

        Assert.Equal(HomeNetworkSettings.Off, read);
        Assert.False(read.Enabled);
    }

    [Fact]
    public async Task What_is_saved_is_read_back_and_a_later_save_replaces_it()
    {
        await SaveAsync(new HomeNetworkSettings(true, ["100.64.0.0/10", "fd00::/8"], ["192.168.1.0/28"]));
        var first = await _store.WithUnitOfWork(uow => _settings.GetAsync(uow));
        await SaveAsync(new HomeNetworkSettings(false, [], []));
        var second = await _store.WithUnitOfWork(uow => _settings.GetAsync(uow));

        Assert.True(first.Enabled);
        Assert.Equal(["100.64.0.0/10", "fd00::/8"], first.ExtraNetworks);
        Assert.Equal(["192.168.1.0/28"], first.AllowList);
        Assert.False(second.Enabled);
        Assert.Empty(second.ExtraNetworks);
        Assert.Empty(second.AllowList);
        Assert.Equal(1, await _store.Scalar("SELECT count(*) FROM home_network_sign_in"));
    }

    [Fact]
    public async Task A_local_client_is_the_accounts_own_identity_for_that_request_and_nothing_is_stored()
    {
        await AddAdminAsync();
        await SaveAsync(new HomeNetworkSettings(true, [], []));

        var identity = await _store.WithUnitOfWork(uow => SignIn().IdentifyAsync(uow, "192.168.1.20", null));

        Assert.NotNull(identity);
        Assert.Equal("alice", identity.User.Username);
        Assert.True(identity.ByHomeNetwork);
        Assert.Equal(HomeNetworkSignIn.SessionId, identity.Session.Id);
        Assert.Equal(identity.User.Id, identity.Session.UserId);
        Assert.Equal(0, await _store.Scalar("SELECT count(*) FROM user_sessions"));
    }

    [Fact]
    public async Task Nobody_is_let_in_while_it_is_off_or_for_a_client_that_is_not_local()
    {
        await AddAdminAsync();

        Assert.Null(await _store.WithUnitOfWork(uow => SignIn().IdentifyAsync(uow, "192.168.1.20", null)));

        await SaveAsync(new HomeNetworkSettings(true, [], []));
        Assert.Null(await _store.WithUnitOfWork(uow => SignIn().IdentifyAsync(uow, "203.0.113.5", "192.168.1.20")));
        Assert.Null(await _store.WithUnitOfWork(uow => SignIn().IdentifyAsync(uow, null, null)));
    }

    [Fact]
    public async Task Nobody_is_let_in_when_there_is_no_active_admin_to_be()
    {
        await SaveAsync(new HomeNetworkSettings(true, [], []));
        Assert.Null(await _store.WithUnitOfWork(uow => SignIn().IdentifyAsync(uow, "192.168.1.20", null)));

        await AddAdminAsync(active: false);
        Assert.Null(await _store.WithUnitOfWork(uow => SignIn().IdentifyAsync(uow, "192.168.1.20", null)));
    }

    [Fact]
    public async Task A_trusted_proxy_is_read_from_the_configured_list_and_a_bad_entry_in_it_trusts_nothing()
    {
        await AddAdminAsync();
        await SaveAsync(new HomeNetworkSettings(true, [], []));
        using var trusting = new StoreFixture(("WEIR_TRUSTED_PROXY_IPS", "10.0.0.2"));

        var viaProxy = await _store.WithUnitOfWork(uow => SignIn(trusting.Options).IdentifyAsync(uow, "10.0.0.2", "203.0.113.5"));
        var homeViaProxy = await _store.WithUnitOfWork(uow => SignIn(trusting.Options).IdentifyAsync(uow, "10.0.0.2", "192.168.1.9"));
        var notTrusted = await _store.WithUnitOfWork(uow => SignIn(_store.Options with { TrustedProxyIps = ["banana"] }).IdentifyAsync(uow, "10.0.0.2", "203.0.113.5"));

        Assert.Null(viaProxy);
        Assert.NotNull(homeViaProxy);
        Assert.NotNull(notTrusted);
    }
}
