using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>The bundled server as the tray's controller sees it: where it is, why it could not start, and how to start, watch, move and stop it.</summary>
interface ITrayServer : IServerListenScope
{
    /// <summary>Raised, on whichever thread changed it, when <see cref="Phase"/> changes.</summary>
    event Action? PhaseChanged;

    int Port { get; }

    ServerPhase Phase { get; }

    DateTime? StartedAtUtc { get; }

    /// <summary>The server process this host started, while it runs, or null.</summary>
    RunningServer? Running { get; }

    bool ServerIsRunning { get; }

    StartupError? StartupError { get; }

    StartupError? GaveUpError { get; }

    int GaveUpAfterAttempts { get; }

    int GaveUpPortInUse { get; }

    bool NoPortChosen { get; }

    /// <summary>Finds the port when it was not known at the start, at the start of each attempt, until it is.</summary>
    Func<CancellationToken, Task<PortChoice.Resolution>>? ResolvePort { set; }

    void PrepareEnvironment();

    void WritePortFile();

    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the server and starts it again with the whole budget of attempts; whether it is back and ready.</summary>
    Task<bool> RestartNowAsync(CancellationToken cancellationToken);

    void Watch(Action onGaveUp, CancellationToken cancellationToken);

    Task StopAsync();

    Task<PortMove> MoveToPortAsync(int to, CancellationToken cancellationToken);
}
