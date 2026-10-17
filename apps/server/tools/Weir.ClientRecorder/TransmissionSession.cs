using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Weir.ArrRecorder;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ClientRecorder;

/// <summary>
/// Transmission. It starts open, so Weir is recorded first against a client that asks for no sign-in; the session then switches RPC
/// authentication on through <c>session-set</c> and records the sign-in, the wrong password and a connection with no credentials saved.
/// </summary>
internal sealed class TransmissionSession(ClientSettings settings) : ClientSession(settings)
{
    private const string User = "recorder";
    private const string Password = "recorder-transmission-secret";

    private string _sessionId = string.Empty;

    protected override async Task<ClientSetup> SetUpAsync()
    {
        string version = string.Empty;
        await Wait.UntilAsync("Transmission to answer", TimeSpan.FromMinutes(3), async () =>
        {
            var session = await RpcAsync(null, new JsonObject { ["method"] = "session-get", ["arguments"] = new JsonObject { ["fields"] = new JsonArray("version") } }).ConfigureAwait(false);
            version = ((string?)session?["arguments"]?["version"] ?? string.Empty).Split(' ')[0];
            return version.Length > 0;
        }).ConfigureAwait(false);

        return new ClientSetup(
            Settings.Version ?? version,
            Connection(DownloadClientKinds.Transmission, Settings.Url, User, Password, null),
            Connection(DownloadClientKinds.Transmission, Settings.Url, User, "not-the-password", null),
            [Password]);
    }

    protected override IDownloadClientPort CreatePort(IManagerHttpHandlerFactory transport) => new TransmissionPort(transport);

    protected override async Task RecordAsync()
    {
        var open = Connection(DownloadClientKinds.Transmission, Settings.Url, null, null, null);
        await TestStepAsync("test-open", "The connection test against a Transmission that asks for no sign-in.", open, expectConnected: true).ConfigureAwait(false);
        await FoldersStepAsync(
            "folders", "Reading the folder Transmission saves downloads in.", open, folders => folders.CompletedFolder is { Length: > 0 }, "its download folder").ConfigureAwait(false);

        await TryAsync("RPC authentication", async () => await RpcAsync(
            null,
            new JsonObject
            {
                ["method"] = "session-set",
                ["arguments"] = new JsonObject { ["rpc-authentication-required"] = true, ["rpc-username"] = User, ["rpc-password"] = Password },
            }).ConfigureAwait(false)).ConfigureAwait(false);

        await TestStepAsync("test", "The connection test with the right user name and password.", Prepared.Right, expectConnected: true).ConfigureAwait(false);
        await TestStepAsync("test-wrong-password", "The connection test with a password Transmission does not know.", Prepared.Wrong, expectConnected: false).ConfigureAwait(false);
        await TestStepAsync("test-no-credentials", "The connection test with no user name or password saved, against a Transmission that wants them.", open, expectConnected: false).ConfigureAwait(false);
    }

    /// <summary>One RPC call by the recorder, with the 409 handshake and the sign-in when there is one. Not part of any recording.</summary>
    private async Task<JsonNode?> RpcAsync(AuthenticationHeaderValue? signIn, JsonObject body)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/transmission/rpc") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = signIn;
            if (_sessionId.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", _sessionId);
            }

            using var response = await Setup.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict && response.Headers.TryGetValues("X-Transmission-Session-Id", out var ids))
            {
                _sessionId = ids.First();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RecorderException($"Transmission's RPC answered {(int)response.StatusCode}: {ArrApi.Excerpt(text)}");
            }

            return JsonNode.Parse(text);
        }

        throw new RecorderException("Transmission kept refusing the session id it gave.");
    }
}
