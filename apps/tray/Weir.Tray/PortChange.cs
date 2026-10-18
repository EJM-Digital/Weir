namespace Weir.Tray;

/// <summary>How a move to another port ended.</summary>
enum PortMove
{
    /// <summary>The server is on the new port and the port is saved.</summary>
    Moved,

    /// <summary>The server could not take the new port, and is where it was (or nothing could be done), with nothing saved.</summary>
    KeptOldPort,

    /// <summary>The server could not take the new port and could not go back either, so it is stopped.</summary>
    Stopped,
}

/// <summary>
/// "Change port (...)...": asks which port, moves the server there, and tells the person how it went. Moving the port is not
/// asking to open Weir, so no browser opens; the balloon offers the new address and a click on it opens it. Everything here
/// runs on the UI thread; only the move waits, and the menu stays responsive meanwhile.
/// </summary>
/// <param name="currentPort">The port the server is on now.</param>
/// <param name="ask">Asks the person which port; null when they close the question without choosing.</param>
/// <param name="moveTo">Restarts the server on a port, going back if it does not come up; how the move ended.</param>
/// <param name="balloon">Shows a balloon: title, text, icon and what a click on it does.</param>
/// <param name="openWeir">What a click on the balloon about the new address does.</param>
/// <param name="showLog">What a click on the balloon about a failed move does: shows tray-host.log.</param>
/// <param name="changed">Called when <see cref="MovingTo"/> changes, so the tray draws the menu again.</param>
/// <param name="cancellationToken">Cancelled when the tray is ending.</param>
sealed class PortChange(
    Func<int> currentPort,
    Func<PortPrompt, int?> ask,
    Func<int, CancellationToken, Task<PortMove>> moveTo,
    Action<string, string, ToolTipIcon, Action?> balloon,
    Action openWeir,
    Action showLog,
    Action changed,
    CancellationToken cancellationToken)
{
    /// <summary>The port a move is under way to, or null.</summary>
    internal int? MovingTo { get; private set; }

    internal async Task RunAsync()
    {
        var current = currentPort();
        var chosen = ask(new PortPrompt(PortPromptReason.Change, current, CurrentPortInUse: false, Suggested: current));
        if (chosen is not { } port || port == current)
        {
            TrayLog.Write("Change port: closed without a new port.");
            return;
        }

        MovingTo = port;
        changed();
        try
        {
            // Restarting waits up to a minute for the new server; this method carries on here when it is done.
            switch (await moveTo(port, cancellationToken))
            {
                case PortMove.Moved:
                    balloon("Weir", TrayBalloons.PortMovedText(port), ToolTipIcon.Info, openWeir);
                    break;
                case PortMove.KeptOldPort:
                    balloon(
                        "Weir",
                        $"Weir could not start on port {port}, so it is still at port {current}. See tray-host.log in the data folder.",
                        ToolTipIcon.Warning,
                        showLog);
                    break;
                default:
                    balloon(
                        "Weir",
                        $"Weir could not start on port {port} or go back to port {current}, so it has stopped. Choose Restart Weir once the port is free. See tray-host.log in the data folder.",
                        ToolTipIcon.Warning,
                        showLog);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            TrayLog.Write($"Change port: stopped before port {port} was ready, because Weir is quitting or updating.");
        }
        finally
        {
            MovingTo = null;
            changed();
        }
    }
}
