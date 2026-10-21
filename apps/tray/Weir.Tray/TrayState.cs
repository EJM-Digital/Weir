using System.Globalization;
using Weir.Tray.LanAccess;

namespace Weir.Tray;

/// <summary>Where the bundled server is, as far as the tray knows.</summary>
enum ServerPhase
{
    /// <summary>Being started, or restarted on purpose; not ready yet.</summary>
    Starting,

    /// <summary>Ready and answering.</summary>
    Running,

    /// <summary>Not running and not coming back by itself: it could not start, or the watchdog gave up.</summary>
    Stopped,

    /// <summary>Asked to stop and finishing its running jobs, which can take most of a minute; on its way to <see cref="Stopped"/>.</summary>
    Stopping,
}

/// <summary>The icon's dot, which shows the health of Weir and of what it is set up to talk to, and nothing else.</summary>
enum TrayDot
{
    /// <summary>Not known yet: the dot blinks until it can be one of the others.</summary>
    Starting,

    /// <summary>Running, and everything Weir relies on answers.</summary>
    Green,

    /// <summary>Running, but a media manager or a folder does not answer.</summary>
    Amber,

    /// <summary>Stopped, crashed, or not coming back by itself.</summary>
    Red,
}

/// <summary>The extra mark in the icon's opposite corner, if any. Pause bars win over the update arrow.</summary>
enum TrayMark
{
    None,
    Paused,
    UpdateReady,
}

/// <summary>One icon the tray can show: its dot, or none while a blinking dot is off, and its mark.</summary>
readonly record struct TrayIconKey(TrayDot? Dot, TrayMark Mark);

/// <summary>
/// What the tray says about Weir right now: its dot, its mark and its hover text. The dot is the platform's health
/// (<see cref="Dot"/>); the mark is pause, or else an update waiting. The menu's greyed status line is the hover text itself,
/// so the two never disagree, and when the dot is amber or red it says what is wrong. Nothing here counts files.
/// </summary>
/// <param name="Phase">Where the server is.</param>
/// <param name="Server">What tray-status.json says, or null when the server has written none. Used only while the server runs.</param>
/// <param name="UpdateVersion">The version of an update that is downloaded and waiting to install, or null.</param>
/// <param name="Port">The port the server is on.</param>
/// <param name="StatusOverdue">A running server has gone past the deadline without a readable status (<see cref="TrayStatusDeadline"/>).</param>
/// <param name="StartupError">Why a stopped server could not start, when it said so; shown in place of the general "Stopped".</param>
sealed record TrayState(
    ServerPhase Phase,
    TrayStatus? Server,
    string? UpdateVersion,
    int Port,
    bool StatusOverdue = false,
    StartupError? StartupError = null)
{
    /// <summary>The longest text a notification-area icon can show on Windows.</summary>
    internal const int HoverLimit = 127;

    private const string Product = "Weir";
    private const string Ellipsis = "…";

    private TrayStatus? Reported => Phase == ServerPhase.Running ? Server : null;

    internal bool IsPaused => Reported is { Paused: true };

    /// <summary>
    /// Red only when the server is stopped for good (the watchdog gave up, or it could not start). Amber when something it
    /// relies on does not answer, or when a running server has no readable status past the deadline. Green otherwise. Blinking
    /// until it can say: while it starts, and while a server that says it is stopping on its own is started again.
    /// </summary>
    internal TrayDot Dot =>
        Phase == ServerPhase.Stopped ? TrayDot.Red
        : Reported is not { } status ? (IsStatusOverdue ? TrayDot.Amber : TrayDot.Starting)
        : !status.ServerOk ? TrayDot.Starting
        : status.SomethingNotAnswering ? TrayDot.Amber
        : TrayDot.Green;

    private bool IsStatusOverdue => Phase == ServerPhase.Running && StatusOverdue;

    internal TrayMark Mark =>
        IsPaused ? TrayMark.Paused
        : UpdateVersion is not null ? TrayMark.UpdateReady
        : TrayMark.None;

    /// <summary>The icon to show: a blinking dot is drawn only while <paramref name="blinkLit"/>.</summary>
    internal TrayIconKey IconKey(bool blinkLit) => new(Dot == TrayDot.Starting && !blinkLit ? null : Dot, Mark);

    /// <summary>The state in a line, without the product name: what Weir is doing, then what does not answer.</summary>
    internal string StatusLine
    {
        get
        {
            var causes = Causes;
            return string.Join(" - ", Headline(causes).Concat(causes));
        }
    }

    /// <summary>The icon's hover text: the product name and the status line, never over <see cref="HoverLimit"/> characters.</summary>
    internal string HoverText => Fit($"{Product} - {StatusLine}");

    internal string Address => AddressFor(Port);

    internal static string AddressFor(int port) => string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}");

    /// <summary>
    /// The address "Copy address" puts on the clipboard: with other devices allowed in, the one they would type, this PC's
    /// name; otherwise this PC's own.
    /// </summary>
    internal static string AddressToCopy(ListenScope scope, int port, string machineName) =>
        scope == ListenScope.OtherDevices
            ? string.Create(CultureInfo.InvariantCulture, $"http://{machineName}:{port}")
            : AddressFor(port);

    // What is wrong stands in for "Running at ...": a running Weir that cannot reach Deluno is not simply running.
    private IEnumerable<string> Headline(List<string> causes) => this switch
    {
        { Phase: ServerPhase.Stopped, StartupError: { } why } => [$"Stopped - {why.Headline} - choose Restart Weir"],
        { Phase: ServerPhase.Stopped } => ["Stopped - choose Restart Weir"],
        { Phase: ServerPhase.Stopping } => ["Stopping..."],
        { IsStatusOverdue: true, Reported: null } => ["Can't read its status"],
        { Dot: TrayDot.Starting } => ["Starting..."],
        { IsPaused: true } => ["Paused"],
        { UpdateVersion: { } version } => [$"Update ready ({version})"],
        _ => causes.Count > 0 ? [] : [$"Running at {Address}"],
    };

    private List<string> Causes
    {
        get
        {
            var causes = new List<string>();
            if (Reported is not { ServerOk: true } status)
            {
                return causes;
            }
            if (status.ManagersUnreachable.Count > 0)
            {
                causes.Add(status.ManagersUnreachable.Count == 1
                    ? $"{status.ManagersUnreachable[0]} isn't answering"
                    : $"{status.ManagersUnreachable.Count} media managers aren't answering");
            }
            if (status.FoldersUnreachable.Count > 0)
            {
                causes.Add(status.FoldersUnreachable.Count == 1
                    ? $"{status.FoldersUnreachable[0]} can't be reached"
                    : $"{status.FoldersUnreachable.Count} folders can't be reached");
            }
            return causes;
        }
    }

    // Cut at a word, never in the middle of one, so a long connection name costs the end of the line and nothing else. The text starts
    // with the product name and a space, so there is always a word to cut after.
    private static string Fit(string text)
    {
        if (text.Length <= HoverLimit)
        {
            return text;
        }
        var cut = text.LastIndexOf(' ', HoverLimit - Ellipsis.Length);
        var kept = text.AsSpan(0, cut).TrimEnd(" -");
        return string.Concat(kept, Ellipsis);
    }
}
