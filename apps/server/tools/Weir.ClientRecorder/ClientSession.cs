using Weir.ArrRecorder;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ClientRecorder;

/// <summary>What a client's set-up leaves for the recording: the version it reported, and the connections to sign in with.</summary>
/// <param name="Version">The client's own version, which names the folder the recordings go in.</param>
/// <param name="Right">A connection with the credentials the client was set up with.</param>
/// <param name="Wrong">A connection with credentials the client does not know.</param>
/// <param name="Secrets">Every secret the session used, scrubbed from the recordings.</param>
internal sealed record ClientSetup(string Version, DownloadClientConnection Right, DownloadClientConnection Wrong, IReadOnlyList<string> Secrets);

/// <summary>
/// One recorded session with a real download client. It sets the client up through the client's own API, then drives Weir's own client
/// code (the <see cref="IDownloadClientPort"/> for its kind) against it, so a recording holds exactly what Weir sent and what the client
/// answered. Steps that fail are reported and the session carries on, so one drifted reply does not hide the others.
/// </summary>
internal abstract class ClientSession : IDisposable
{
    /// <summary>The request headers that carry a sign-in, beside the ones a manager's recording keeps.</summary>
    private static readonly string[] SignInRequestHeaders = ["authorization", "cookie", "x-transmission-session-id"];

    private static readonly string[] SignInReplyHeaders = ["set-cookie", "x-transmission-session-id"];

    private readonly ExchangeLog _log = new();
    private readonly List<string> _failures = [];
    private RecordingWriter _writer = null!;

    protected ClientSession(ClientSettings settings)
    {
        Settings = settings;
        Setup = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new Uri(settings.Url), Timeout = TimeSpan.FromSeconds(60) };
    }

    protected ClientSettings Settings { get; }

    /// <summary>The recorder's own calls to the client, which are not part of any recording. Cookies are the caller's to carry.</summary>
    protected HttpClient Setup { get; }

    protected IDownloadClientPort Port { get; private set; } = null!;

    protected ClientSetup Prepared { get; private set; } = null!;

    public static ClientSession For(ClientSettings settings) => settings.Kind switch
    {
        DownloadClientKinds.QBittorrent => new QBittorrentSession(settings),
        DownloadClientKinds.Transmission => new TransmissionSession(settings),
        DownloadClientKinds.Deluge => new DelugeSession(settings),
        DownloadClientKinds.Sabnzbd => new SabnzbdSession(settings),
        _ => new NzbgetSession(settings),
    };

    public void Dispose() => Setup.Dispose();

    public async Task<int> RunAsync()
    {
        Prepared = await SetUpAsync().ConfigureAwait(false);
        Say($"{Settings.Kind} {Prepared.Version} is up");
        _writer = new RecordingWriter(Settings.Kind, Prepared.Version, Settings.OutputFolder, Prepared.Secrets);
        var transport = new RecordingTransport(
            new SocketsManagerHttpHandlerFactory(),
            _log,
            [.. RecordingTransport.ManagerRequestHeaders, .. SignInRequestHeaders],
            [.. RecordingTransport.ManagerReplyHeaders, .. SignInReplyHeaders]);
        Port = CreatePort(transport);

        await RecordAsync().ConfigureAwait(false);

        Say(_failures.Count == 0 ? "Every step recorded." : $"{_failures.Count} step(s) failed:\n  " + string.Join("\n  ", _failures));
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Waits for the client, sets it up, and says what it is.</summary>
    protected abstract Task<ClientSetup> SetUpAsync();

    protected abstract IDownloadClientPort CreatePort(IManagerHttpHandlerFactory transport);

    /// <summary>Every recorded step of the session, in order.</summary>
    protected abstract Task RecordAsync();

    protected static DownloadClientConnection Connection(string kind, string baseUrl, string? username, string? password, string? apiKey) =>
        new(kind, "recorder", baseUrl, username, password, apiKey);

    protected static void Say(string line) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}");

    /// <summary>The client's connection test, recorded as <paramref name="state"/>.</summary>
    protected Task TestStepAsync(string state, string note, DownloadClientConnection connection, bool expectConnected) =>
        StepAsync(state, note, async () =>
        {
            var (ok, detail) = await Port.TestAsync(connection).ConfigureAwait(false);
            Say($"  {detail}");
            Expect(ok == expectConnected, $"{state}: the connection test should {(expectConnected ? "pass" : "fail")}: {detail}");
        });

    /// <summary>Reading the client's folders, recorded as <paramref name="state"/>.</summary>
    protected Task FoldersStepAsync(string state, string note, DownloadClientConnection connection, Func<DownloadClientFolders, bool> shouldBe, string shouldBeText) =>
        StepAsync(state, note, async () =>
        {
            var folders = await Port.ReadFoldersAsync(connection).ConfigureAwait(false);
            Say($"  completed folder: {folders.CompletedFolder ?? "none"}; categories: {string.Join(", ", folders.CategoryFolders.Select(pair => $"{pair.Category}={pair.Folder}"))}");
            Expect(shouldBe(folders), $"{state}: Weir should read {shouldBeText}");
        });

    protected async Task StepAsync(string state, string note, Func<Task> run)
    {
        _log.Clear();
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fail(state, exception);
            return;
        }

        var exchanges = _log.Snapshot();
        if (exchanges.Count == 0)
        {
            Fail(state, new RecorderException("Weir made no call"));
            return;
        }

        _writer.Write(state, note, exchanges);
        Say($"recorded {state}: {exchanges.Count} exchange(s)");
    }

    /// <summary>A set-up call that may fail: the recording then shows the client as it was left, and the failure is reported at the end.</summary>
    protected async Task TryAsync(string what, Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(false);
            Say($"set up: {what}");
        }
        catch (Exception exception) when (exception is HttpRequestException or RecorderException or TaskCanceledException)
        {
            _failures.Add($"set-up step '{what}' failed: {exception.Message}");
            Say($"SET-UP FAILED {what}: {exception.Message}");
        }
    }

    protected void Expect(bool condition, string problem)
    {
        if (!condition)
        {
            _failures.Add(problem);
            Say($"  NOT AS EXPECTED: {problem}");
        }
    }

    /// <summary>Sends a set-up request; anything but a success is a <see cref="RecorderException"/> naming the call.</summary>
    protected async Task<SetUpReply> SendAsync(HttpRequestMessage request, string what)
    {
        using (request)
        {
            using var response = await Setup.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new RecorderException($"{what} answered {(int)response.StatusCode}: {ArrApi.Excerpt(text)}");
            }

            var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.First().Split(';')[0] : null;
            var sessionId = response.Headers.TryGetValues("X-Transmission-Session-Id", out var ids) ? ids.First() : null;
            return new SetUpReply(text, cookie, sessionId);
        }
    }

    private void Fail(string step, Exception exception)
    {
        _failures.Add($"{step}: {exception.Message}");
        Say($"FAILED {step}: {exception}");
    }
}

/// <summary>What a set-up call answered: the body, the session cookie it set and the Transmission session id it named.</summary>
internal sealed record SetUpReply(string Text, string? Cookie, string? SessionId);
