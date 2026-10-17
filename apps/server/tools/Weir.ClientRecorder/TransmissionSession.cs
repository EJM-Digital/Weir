using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Weir.ArrRecorder;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ClientRecorder;

/// <summary>
/// Transmission, in the two ways it is run: with no sign-in (the address in <c>RECORDER_URL</c>) and with a user name and password (the
/// address in <c>RECORDER_AUTH_URL</c>, a second copy started with them). It has no settings to change over its API that would switch the
/// sign-in on, so there are two copies.
/// </summary>
internal sealed class TransmissionSession(ClientSettings settings) : ClientSession(settings)
{
    private const string User = "recorder";
    private const string Password = "recorder-transmission-secret";

    private string AuthUrl => Settings.AuthUrl ?? throw new InvalidOperationException("RECORDER_AUTH_URL is not set; it is where the Transmission that asks for a sign-in listens.");

    protected override async Task<ClientSetup> SetUpAsync()
    {
        var version = string.Empty;
        await Wait.UntilAsync("Transmission to answer", TimeSpan.FromMinutes(3), async () =>
            (await VersionAsync(Settings.Url, null).ConfigureAwait(false)).Length > 0).ConfigureAwait(false);
        await Wait.UntilAsync("the Transmission that asks for a sign-in to answer", TimeSpan.FromMinutes(3), async () =>
            (version = await VersionAsync(AuthUrl, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")))).ConfigureAwait(false)).Length > 0).ConfigureAwait(false);

        return new ClientSetup(
            Settings.Version ?? version,
            Connection(DownloadClientKinds.Transmission, AuthUrl, User, Password, null),
            Connection(DownloadClientKinds.Transmission, AuthUrl, User, "not-the-password", null),
            [Password]);
    }

    protected override IDownloadClientPort CreatePort(IManagerHttpHandlerFactory transport) => new TransmissionPort(transport);

    protected override async Task RecordAsync()
    {
        await TestStepAsync(
            "test-open",
            "The connection test against a Transmission that asks for no sign-in.",
            Connection(DownloadClientKinds.Transmission, Settings.Url, null, null, null),
            expectConnected: true).ConfigureAwait(false);
        await TestStepAsync("test", "The connection test with the right user name and password.", Prepared.Right, expectConnected: true).ConfigureAwait(false);
        await TestStepAsync("test-wrong-password", "The connection test with a password Transmission does not know.", Prepared.Wrong, expectConnected: false).ConfigureAwait(false);
        await TestStepAsync(
            "test-no-credentials",
            "The connection test with no user name or password saved, against a Transmission that asks for them.",
            Connection(DownloadClientKinds.Transmission, AuthUrl, null, null, null),
            expectConnected: false).ConfigureAwait(false);
        await FoldersStepAsync(
            "folders", "Reading the folder Transmission saves downloads in.", Prepared.Right, folders => folders.CompletedFolder is { Length: > 0 }, "its download folder").ConfigureAwait(false);
    }

    /// <summary>The version the Transmission at <paramref name="url"/> reports, or empty when it does not let this call in. Not part of any recording.</summary>
    private async Task<string> VersionAsync(string url, AuthenticationHeaderValue? signIn)
    {
        var body = new JsonObject { ["method"] = "session-get", ["arguments"] = new JsonObject { ["fields"] = new JsonArray("version") } };
        var sessionId = string.Empty;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(url), "/transmission/rpc"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = signIn;
            if (sessionId.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", sessionId);
            }

            using var response = await Setup.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict && response.Headers.TryGetValues("X-Transmission-Session-Id", out var ids))
            {
                sessionId = ids.First();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RecorderException($"Transmission's RPC answered {(int)response.StatusCode}: {ArrApi.Excerpt(text)}");
            }

            return ((string?)JsonNode.Parse(text)?["arguments"]?["version"] ?? string.Empty).Split(' ')[0];
        }

        throw new RecorderException("Transmission kept refusing the session id it gave.");
    }
}
