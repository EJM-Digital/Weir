using System.Text;
using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Transmission, read only: its RPC at <c>{base_url}/transmission/rpc</c> requires a CSRF-style handshake —
/// the first request is refused with 409 and an <c>X-Transmission-Session-Id</c> header, which the retried
/// request must carry. <c>session-get</c>'s <c>download-dir</c> is the only folder Transmission has; it has no
/// category concept.
/// </summary>
public sealed class TransmissionPort : IDownloadClientPort
{
    private const string SessionIdHeader = "X-Transmission-Session-Id";

    private readonly IManagerHttpHandlerFactory _handlers;

    public TransmissionPort(IManagerHttpHandlerFactory handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    public string Kind => DownloadClientKinds.Transmission;

    public async Task<(bool Ok, string Detail)> TestAsync(DownloadClientConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            var response = await CallSessionGetAsync(connection, cancellationToken).ConfigureAwait(false);
            if (response.Status is 401 or 403)
            {
                return (false, $"Weir reached {connection.Label}, but the username or password was refused. Check them and save again.");
            }

            return response.Succeeded && response.Json() is WireObject dict && dict.Get("result") is WireString { Value: "success" }
                ? (true, $"Connected. Weir can reach {connection.Label}.")
                : (false, $"Weir reached {connection.Label} but did not get the answer it expected. Check the address points at Transmission itself.");
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
            var response = await CallSessionGetAsync(connection, cancellationToken).ConfigureAwait(false);
            return response.Succeeded ? TransmissionRules.Parse(response.Json()) : DownloadClientFolders.Empty;
        }
        catch (DownloadClientUnreachableException)
        {
            return DownloadClientFolders.Empty;
        }
    }

    /// <summary>Try the call; on 409, retry once with the session id the refusal named. Never a copy-pasted second attempt — one retry helper, called once.</summary>
    private async Task<DownloadClientHttpResponse> CallSessionGetAsync(DownloadClientConnection connection, CancellationToken cancellationToken)
    {
        var client = new DownloadClientHttpClient(connection.BaseUrl, _handlers, connection: connection.Reference);
        var authHeader = AuthHeader(connection);
        var first = await SendSessionGetAsync(client, authHeader, cancellationToken).ConfigureAwait(false);
        if (first.Status != 409)
        {
            return first;
        }

        var sessionId = first.Headers.GetValueOrDefault(SessionIdHeader);
        if (string.IsNullOrEmpty(sessionId))
        {
            return first;
        }

        var retryHeaders = (authHeader ?? []).Append(new KeyValuePair<string, string>(SessionIdHeader, sessionId)).ToList();
        return await SendSessionGetAsync(client, retryHeaders, cancellationToken).ConfigureAwait(false);
    }

    private static Task<DownloadClientHttpResponse> SendSessionGetAsync(
        DownloadClientHttpClient client, IReadOnlyList<KeyValuePair<string, string>>? headers, CancellationToken cancellationToken)
    {
        var body = new WireObject().Set("method", "session-get").Set("arguments", new WireObject().Set("fields", new WireArray([WireValue.Of("download-dir")])));
        return client.SendAsync(HttpMethod.Post, "/transmission/rpc", content: DownloadClientHttpClient.JsonContent(body), headers: headers, cancellationToken: cancellationToken);
    }

    private static List<KeyValuePair<string, string>>? AuthHeader(DownloadClientConnection connection)
    {
        if (string.IsNullOrEmpty(connection.Username) && string.IsNullOrEmpty(connection.Password))
        {
            return null;
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Username}:{connection.Password}"));
        return [new("Authorization", $"Basic {token}")];
    }
}
