using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>Weir's manager transport answering from recordings of a real Sonarr or Radarr.</summary>
internal sealed class ArrReplay : IManagerHttpHandlerFactory, IDisposable
{
    private readonly ArrReplayHandler _handler;

    public ArrReplay(string app, params ArrRecording[] recordings)
    {
        App = app;
        _handler = new ArrReplayHandler(recordings);
    }

    public string App { get; }

    public IReadOnlyList<ArrRequest> Sent => _handler.Sent;

    /// <summary>A connection to the app being replayed, with a key the recording never saw.</summary>
    public ManagerConnection Connection(string apiKey = "a-key-the-recording-never-saw") => new(App, "recorded", "http://localhost:8989", apiKey);

    public IMediaManagerPort Port() => new HttpMediaManagerPorts(this).PortForKind(App)!;

    public void AssertSentAsRecorded(ArrRecording recording) => _handler.AssertSentAsRecorded(recording);

    public void Dispose() => _handler.Dispose();

    public HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local) => _handler;
}
