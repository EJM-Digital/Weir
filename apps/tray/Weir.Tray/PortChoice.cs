using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Weir.Tray;

// ---------------------------------------------------------------------------
// Which port Weir listens on, and how that choice is made and kept.
//
// The port is chosen once — by the person, or by an administrator on the command line —
// saved under the runtime home, and reused. It never moves on its own, because every
// bookmark and link to Weir carries it.
// ---------------------------------------------------------------------------

/// <summary>Why the port dialog is being shown.</summary>
enum PortPromptReason
{
    /// <summary>First run on an interactive desktop, nothing supplied.</summary>
    FirstRun,

    /// <summary>The saved port is taken by another program on a later start.</summary>
    SavedPortBusy,

    /// <summary>The user picked "Change port" from the tray menu.</summary>
    Change,
}

/// <summary>What the dialog is asked, and what it should pre-fill.</summary>
sealed record PortPrompt(PortPromptReason Reason, int CurrentPort, bool CurrentPortInUse, int? Suggested);

/// <summary>
/// The outcome of deciding on a port at startup. <see cref="Port"/> is null when the tray
/// should not start (the person quit the dialog, or a headless start found its saved port
/// taken). <see cref="Save"/> says whether the port is a new choice to write to disk.
/// </summary>
sealed record PortDecision(int? Port, bool Save, string Reason);

static class PortChoice
{
    /// <summary>
    /// Weir's own default. It spells W-E-I-R on a phone keypad, is unassigned in the IANA
    /// registry (inside 9347-9373), and no common media-stack app listens on it.
    /// </summary>
    internal const int DefaultPort = 9347;

    /// <summary>How far above a busy port to look for a free one to suggest.</summary>
    internal const int ScanRange = 20;

    /// <summary>The environment variable an administrator can set instead of <c>--port</c>.</summary>
    internal const string PortEnvironmentVariable = "WEIR_PORT";

    internal const string SavedPortFileName = "port.txt";

    /// <summary>
    /// A port supplied without a person: <c>--port N</c> on the command line (which wins),
    /// otherwise <c>WEIR_PORT</c>. Returns the raw text and where it came from, or null.
    /// </summary>
    internal static (string Text, string Source)? SuppliedPort(IReadOnlyList<string> args, Func<string, string?> getEnv)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase))
            {
                return (i + 1 < args.Count ? args[i + 1] : "", "--port");
            }

            if (args[i].StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
            {
                return (args[i]["--port=".Length..], "--port");
            }
        }

        var env = getEnv(PortEnvironmentVariable);
        return string.IsNullOrWhiteSpace(env) ? null : (env, PortEnvironmentVariable);
    }

    /// <summary>A whole number from 1 to 65535, or null.</summary>
    internal static int? ParsePort(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535
            ? port
            : null;
    }

    /// <summary>
    /// What is wrong with a port typed into the dialog, in words a person can act on, or null
    /// if it is fine. <paramref name="allowedInUse"/> is Weir's own current port when changing
    /// it: Weir's server is the program holding it, so it is not "in use by another program".
    /// </summary>
    internal static string? Validate(string? text, int? allowedInUse, Func<int, bool> isInUse)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Enter a port number.";
        }

        if (ParsePort(text) is not { } port)
        {
            return "A port is a whole number from 1 to 65535.";
        }

        if (port != allowedInUse && isInUse(port))
        {
            return $"Another program on this computer is already using port {port}. Choose a different number.";
        }

        return null;
    }

    /// <summary>The first port from <paramref name="start"/> upward that is free, or null.</summary>
    internal static int? FirstFree(int start, Func<int, bool> isInUse, int range = ScanRange)
    {
        for (var port = start; port < start + range && port <= 65535; port++)
        {
            if (!isInUse(port))
            {
                return port;
            }
        }
        return null;
    }

    /// <summary>
    /// Decide the port at startup. Pure: every side effect (probing the machine, asking the
    /// person) is passed in, so the whole decision table is testable.
    /// </summary>
    /// <param name="supplied">From <see cref="SuppliedPort"/>.</param>
    /// <param name="saved">The port saved by an earlier run, if any.</param>
    /// <param name="interactive">Whether there is a person at a desktop to ask.</param>
    /// <param name="isInUse">Whether another program holds a port.</param>
    /// <param name="ask">Shows the dialog; returns the chosen port, or null if they quit.</param>
    internal static PortDecision Decide(
        (string Text, string Source)? supplied,
        int? saved,
        bool interactive,
        Func<int, bool> isInUse,
        Func<PortPrompt, int?> ask)
    {
        string? rejectedSupplied = null;
        if (supplied is { } s)
        {
            var port = ParsePort(s.Text);
            if (port is { } p)
            {
                // An administrator said which port. Use it, save it, never ask. If it is busy the
                // server will fail to bind and say so; second-guessing an explicit choice would move
                // Weir to an address nobody chose.
                var busy = isInUse(p) ? " It looks like another program is using it right now, so the server may not be able to start." : "";
                return new PortDecision(p, Save: p != saved, $"Using port {p} from {s.Source}.{busy}");
            }
            rejectedSupplied = $"Ignoring {s.Source} value '{s.Text}': a port is a whole number from 1 to 65535. ";
        }

        if (saved is { } savedPort)
        {
            if (!isInUse(savedPort))
            {
                return new PortDecision(savedPort, Save: false, $"{rejectedSupplied}Using saved port {savedPort}.");
            }

            if (!interactive)
            {
                return new PortDecision(null, Save: false,
                    $"{rejectedSupplied}Saved port {savedPort} is in use by another program. Weir does not move to a different "
                    + "port on its own, because that would break every bookmark and link to it, and there is nobody at a "
                    + $"desktop to ask. Free port {savedPort}, or start Weir with --port <number> (or set {PortEnvironmentVariable}) "
                    + "to choose another.");
            }

            var suggested = FirstFree(savedPort + 1, isInUse);
            var chosen = ask(new PortPrompt(PortPromptReason.SavedPortBusy, savedPort, CurrentPortInUse: true, suggested));
            return chosen is { } c
                ? new PortDecision(c, Save: c != savedPort, $"{rejectedSupplied}Saved port {savedPort} was in use; the user chose {c}.")
                : new PortDecision(null, Save: false, $"{rejectedSupplied}Saved port {savedPort} was in use and the user quit without choosing another.");
        }

        // First run: nothing supplied (or it was unusable) and nothing saved.
        var defaultBusy = isInUse(DefaultPort);
        if (!interactive)
        {
            var fallback = defaultBusy ? FirstFree(DefaultPort + 1, isInUse) : DefaultPort;
            if (fallback is null)
            {
                return new PortDecision(null, Save: false,
                    $"{rejectedSupplied}No interactive desktop to ask, and ports {DefaultPort}-{DefaultPort + ScanRange - 1} are all in use. "
                    + $"Start Weir with --port <number> (or set {PortEnvironmentVariable}) to choose one.");
            }
            var why = defaultBusy
                ? $"the default port {DefaultPort} is in use, so Weir chose {fallback}, the first free port above it"
                : $"using the default port {DefaultPort}";
            return new PortDecision(fallback, Save: true,
                $"{rejectedSupplied}First run with no interactive desktop to ask: {why}. Saved; Weir will use port {fallback} from now on. "
                + $"To choose a different port, start Weir with --port <number> or set {PortEnvironmentVariable}.");
        }

        var firstChoice = ask(new PortPrompt(
            PortPromptReason.FirstRun,
            DefaultPort,
            CurrentPortInUse: defaultBusy,
            defaultBusy ? FirstFree(DefaultPort + 1, isInUse) : DefaultPort));
        return firstChoice is { } f
            ? new PortDecision(f, Save: true, $"{rejectedSupplied}First run: the user chose port {f}.")
            : new PortDecision(null, Save: false, $"{rejectedSupplied}First run: the user quit the port dialog without choosing.");
    }

    // -- Persistence ----------------------------------------------------------

    /// <summary>What a look at the saved port found.</summary>
    /// <param name="Busy">The file stayed unreadable (another process was replacing it or scanning it). That says nothing about the port: it is not "never chosen".</param>
    /// <param name="Port">The saved port, or null when none is saved or the file holds no port.</param>
    internal readonly record struct SavedPort(bool Busy, int? Port);

    /// <summary>How many times a file that stays unreadable is looked at (each look is several, see <see cref="ReplacedFile"/>), and how far apart.</summary>
    internal const int BusyLooks = 10;

    internal static readonly TimeSpan BusyLookAgainAfter = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The port an earlier run saved. A port.txt that cannot be read is waited for, about two seconds, and then reported as busy; it is
    /// never taken for "no port chosen", because that puts the first-run dialog in front of someone who already answered it and saves
    /// a different port over theirs.
    /// </summary>
    internal static SavedPort LoadSaved(string runtimeHome, Action<TimeSpan>? pause = null)
    {
        pause ??= Thread.Sleep;
        var path = Path.Combine(runtimeHome, SavedPortFileName);
        for (var look = 1; ; look++)
        {
            var reading = ReplacedFile.ReadText(path, pause: pause);
            switch (reading.State)
            {
                case ReplacedFileState.Absent:
                    return new SavedPort(false, null);
                case ReplacedFileState.Read:
                    return new SavedPort(false, ParsePort(reading.Text));
            }

            if (look == BusyLooks)
            {
                TrayLog.Write($"{SavedPortFileName} could not be read (it stayed busy); the saved port is not known.");
                return new SavedPort(true, null);
            }

            pause(BusyLookAgainAfter);
        }
    }

    // Written whole (AtomicFile): a torn write would read as "never chosen" and put the first-run dialog in
    // front of someone who already answered it.
    internal static void Save(string runtimeHome, int port) =>
        AtomicFile.WriteAllText(runtimeHome, SavedPortFileName, port.ToString(CultureInfo.InvariantCulture));

    // -- The machine ----------------------------------------------------------

    /// <summary>
    /// Whether a person can answer a dialog. False for a service, a WinRM or SSH session,
    /// or anything else in session 0 — where a modal dialog would wait forever for nobody.
    /// </summary>
    internal static bool HasInteractiveDesktop()
    {
        try
        {
            return Environment.UserInteractive
                && System.Diagnostics.Process.GetCurrentProcess().SessionId != 0
                && SystemInformation.UserInteractive;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Not knowing whether anyone is there means not asking: a dialog nobody can answer would hang the start.
            TrayLog.Write($"Could not tell whether this is an interactive desktop ({ex.Message}); treating it as not.");
            return false;
        }
    }

    /// <summary>
    /// Whether another program already holds <paramref name="port"/>. The server can be moved between this PC only
    /// and every interface without changing its port, so this checks the listener table and then tries an exclusive
    /// bind on 0.0.0.0 — a port Windows has reserved (Hyper-V and WSL reserve ranges) fails that too.
    /// </summary>
    internal static bool IsInUse(int port)
    {
        try
        {
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port))
            {
                return true;
            }
        }
        catch (NetworkInformationException ex)
        {
            // The bind below still answers the question.
            TrayLog.Write($"Could not read the TCP listener table ({ex.Message}); checking port {port} by binding it.");
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true,
            };
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
