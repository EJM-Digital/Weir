using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Net;
using Weir.Core.Time;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Auth;

/// <summary>
/// Decides whether a request with no session is already signed in because it comes from the home network, and as whom. The answer
/// is for that request only: nothing is stored and no cookie or token is issued.
/// </summary>
public sealed class HomeNetworkSignIn
{
    /// <summary>The id of the session a skipped visitor is shown with; it is never stored.</summary>
    public const string SessionId = "home-network";

    private readonly HomeNetworkSignInStore _settings;
    private readonly AuthStore _users;
    private readonly TimeProvider _time;
    private readonly List<NetRange> _trustedProxies;

    public HomeNetworkSignIn(HomeNetworkSignInStore settings, AuthStore users, WeirOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _settings = settings;
        _users = users;
        _time = time;
        _trustedProxies = [.. options.TrustedProxyIps
            .Select(value => NetRange.TryParse(value.Trim(), strict: false, out var network) ? network : null)
            .OfType<NetRange>()];
    }

    /// <summary>
    /// The account's own identity when the setting is on, the client is on the home network and the account exists; otherwise
    /// <see langword="null"/>. <paramref name="peer"/> is the socket peer and <paramref name="forwardedFor"/> the
    /// <c>X-Forwarded-For</c> header as they arrived, before any forwarded header was applied.
    /// </summary>
    public async Task<SignedInSession?> IdentifyAsync(UnitOfWork uow, string? peer, string? forwardedFor)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var settings = await _settings.GetAsync(uow).ConfigureAwait(false);
        if (!settings.Enabled || !HomeNetwork.Skips(HomeNetwork.ClientOf(peer, forwardedFor, _trustedProxies), settings))
        {
            return null;
        }

        if (await _users.FindActiveAdminAsync(uow).ConfigureAwait(false) is not { } account)
        {
            return null;
        }

        var now = Timestamp.UtcNow(_time);
        var session = new UserSessionRecord(SessionId, account.Id, string.Empty, now, now, false, now, null, "This device, on the home network");
        return new SignedInSession(session, account, ByHomeNetwork: true);
    }
}
