using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>Weir's download-client transport answering from a recording of a real client.</summary>
internal sealed class ClientReplay : IManagerHttpHandlerFactory, IDisposable
{
    /// <summary>What a recording keeps in place of a secret. A test signs in with it, so the request it sends is the one recorded.</summary>
    public const string Scrubbed = "<scrubbed>";

    /// <summary>The password the recorder tried that the client did not know.</summary>
    public const string WrongPassword = "not-the-password";

    /// <summary>The API key the recorder tried that SABnzbd did not know.</summary>
    public const string WrongKey = "0123456789abcdef0123456789abcdef";

    private readonly ArrReplayHandler _handler;

    public ClientReplay(ArrRecording recording)
    {
        Recording = recording;
        _handler = new ArrReplayHandler(recording);
    }

    public ArrRecording Recording { get; }

    /// <summary>Every request Weir sent, in order.</summary>
    public IReadOnlyList<ArrRequest> Sent => _handler.Sent;

    /// <summary>A connection that signs in as the recorder did, with the secret the recording scrubbed.</summary>
    public static DownloadClientConnection Right(string kind) => kind switch
    {
        DownloadClientKinds.QBittorrent => Connection(kind, "admin", Scrubbed, null),
        DownloadClientKinds.Transmission => Connection(kind, "recorder", Scrubbed, null),
        DownloadClientKinds.Nzbget => Connection(kind, "nzbget", Scrubbed, null),
        DownloadClientKinds.Sabnzbd => Connection(kind, null, null, Scrubbed),
        _ => Connection(kind, null, Scrubbed, null),
    };

    /// <summary>A connection that signs in as the recorder did with a secret the client did not know.</summary>
    public static DownloadClientConnection Wrong(string kind) => kind switch
    {
        DownloadClientKinds.QBittorrent => Connection(kind, "admin", WrongPassword, null),
        DownloadClientKinds.Transmission => Connection(kind, "recorder", WrongPassword, null),
        DownloadClientKinds.Nzbget => Connection(kind, "nzbget", WrongPassword, null),
        DownloadClientKinds.Sabnzbd => Connection(kind, null, null, WrongKey),
        _ => Connection(kind, null, WrongPassword, null),
    };

    /// <summary>A connection with nothing saved to sign in with.</summary>
    public static DownloadClientConnection Anonymous(string kind) => Connection(kind, null, null, null);

    public IDownloadClientPort Port(string kind) => kind switch
    {
        DownloadClientKinds.QBittorrent => new QBittorrentPort(this),
        DownloadClientKinds.Transmission => new TransmissionPort(this),
        DownloadClientKinds.Nzbget => new NzbgetPort(this),
        DownloadClientKinds.Sabnzbd => new SabnzbdPort(this),
        _ => new DelugePort(this),
    };

    /// <summary>Fails unless Weir sent every request in the recording as recorded.</summary>
    public void AssertSentAsRecorded() => _handler.AssertSentAsRecorded(Recording);

    public void Dispose() => _handler.Dispose();

    public HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local) => _handler;

    private static DownloadClientConnection Connection(string kind, string? username, string? password, string? apiKey) =>
        new(kind, "recorded", "http://localhost:8080", username, password, apiKey, 1);
}
