using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// SABnzbd, read only: <c>GET /api?mode=get_config&amp;section=misc</c> for the base completed folder and
/// <c>section=categories</c> for each category's own, both authenticated with the saved API key.
/// </summary>
public sealed class SabnzbdPort : IDownloadClientPort
{
    private readonly IManagerHttpHandlerFactory _handlers;

    public SabnzbdPort(IManagerHttpHandlerFactory handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    public string Kind => DownloadClientKinds.Sabnzbd;

    public async Task<(bool Ok, string Detail)> TestAsync(DownloadClientConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            var client = new DownloadClientHttpClient(connection.BaseUrl, _handlers, connection: connection.Reference);
            var response = await client.SendAsync(HttpMethod.Get, "/api", MiscParameters(connection), cancellationToken: cancellationToken).ConfigureAwait(false);
            return Classify(response, connection);
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
            var misc = await client.SendAsync(HttpMethod.Get, "/api", MiscParameters(connection), cancellationToken: cancellationToken).ConfigureAwait(false);
            var categories = await client.SendAsync(HttpMethod.Get, "/api", CategoriesParameters(connection), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!misc.Succeeded || !categories.Succeeded)
            {
                return DownloadClientFolders.Empty;
            }

            var completeDir = SabnzbdRules.ParseCompleteDir(misc.Json());
            return new DownloadClientFolders(completeDir, SabnzbdRules.ParseCategoryFolders(categories.Json(), completeDir));
        }
        catch (DownloadClientUnreachableException)
        {
            return DownloadClientFolders.Empty;
        }
    }

    private static List<KeyValuePair<string, string>> MiscParameters(DownloadClientConnection connection) => Parameters(connection, "misc");

    private static List<KeyValuePair<string, string>> CategoriesParameters(DownloadClientConnection connection) => Parameters(connection, "categories");

    private static List<KeyValuePair<string, string>> Parameters(DownloadClientConnection connection, string section) =>
    [
        new("mode", "get_config"),
        new("section", section),
        new("output", "json"),
        new("apikey", connection.ApiKey ?? string.Empty),
    ];

    private static (bool Ok, string Detail) Classify(DownloadClientHttpResponse response, DownloadClientConnection connection)
    {
        if (response.Succeeded && response.Json() is WireObject dict && dict.Get("config") is WireObject)
        {
            return (true, $"Connected. Weir can reach {connection.Label}.");
        }

        return response.Status is 401 or 403
            ? (false, $"Weir reached {connection.Label}, but the API key was refused. Check the key and save it again.")
            : (false, $"Weir reached {connection.Label} but did not get the answer it expected. Check the address points at SABnzbd itself.");
    }
}
