using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// qBittorrent, read only: a cookie login (<c>POST /api/v2/auth/login</c>, session in <c>Set-Cookie</c>, read by
/// <see cref="QBittorrentLoginRules"/>) followed by its categories and preferences. The cookie is captured per call
/// rather than kept in a jar across requests, and the login is sent without one, so every read starts a fresh session.
/// </summary>
public sealed class QBittorrentPort : IDownloadClientPort
{
    private readonly IManagerHttpHandlerFactory _handlers;

    public QBittorrentPort(IManagerHttpHandlerFactory handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    public string Kind => DownloadClientKinds.QBittorrent;

    public async Task<(bool Ok, string Detail)> TestAsync(DownloadClientConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            var client = new DownloadClientHttpClient(connection.BaseUrl, _handlers, connection: connection.Reference);
            var (ok, cookie) = await LoginAsync(client, connection, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return (false, $"Weir reached {connection.Label}, but the username or password was refused. Check them and save again.");
            }

            var preferences = await client.SendAsync(
                HttpMethod.Get, "/api/v2/app/preferences", headers: CookieHeader(cookie), cancellationToken: cancellationToken).ConfigureAwait(false);
            return preferences.Succeeded
                ? (true, $"Connected. Weir can reach {connection.Label}.")
                : (false, $"Weir reached {connection.Label} but did not get the answer it expected. Check the address points at qBittorrent itself.");
        }
        catch (DownloadClientUnreachableException)
        {
            return (false, DownloadClientDialectRules.Unreachable(connection));
        }
    }

    public async Task<DownloadClientFolders> ReadFoldersAsync(DownloadClientConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            var client = new DownloadClientHttpClient(connection.BaseUrl, _handlers, connection: connection.Reference);
            var (ok, cookie) = await LoginAsync(client, connection, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return DownloadClientFolders.Empty;
            }

            var headers = CookieHeader(cookie);
            var categories = await client.SendAsync(HttpMethod.Get, "/api/v2/torrents/categories", headers: headers, cancellationToken: cancellationToken).ConfigureAwait(false);
            var preferences = await client.SendAsync(HttpMethod.Get, "/api/v2/app/preferences", headers: headers, cancellationToken: cancellationToken).ConfigureAwait(false);
            return QBittorrentRules.Parse(categories.Json(), preferences.Json());
        }
        catch (DownloadClientUnreachableException)
        {
            return DownloadClientFolders.Empty;
        }
    }

    /// <summary>Older versions answer 200 whether the login succeeded or not, so the body decides; 5.x answers 204, with a session cookie unless the login already carried one (#842, #856).</summary>
    private static async Task<QBittorrentLogin> LoginAsync(DownloadClientHttpClient client, DownloadClientConnection connection, CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = connection.Username ?? string.Empty,
            ["password"] = connection.Password ?? string.Empty,
        });
        var response = await client.SendAsync(HttpMethod.Post, "/api/v2/auth/login", content: form, cancellationToken: cancellationToken).ConfigureAwait(false);
        return QBittorrentLoginRules.Read(response.Status, response.BodyText, response.Headers.GetValueOrDefault("Set-Cookie"));
    }

    private static List<KeyValuePair<string, string>>? CookieHeader(string? cookie) =>
        string.IsNullOrEmpty(cookie) ? null : [new("Cookie", cookie)];
}
