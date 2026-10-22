namespace Weir.Tray;

/// <summary>Where a balloon is drawn: the tray icon's notification.</summary>
interface IBalloonSurface
{
    void Show(int timeoutMs, string title, string text, ToolTipIcon icon);
}

/// <summary>The shell's notification as the place balloons are drawn.</summary>
sealed class ShellBalloons(ITrayShell shell) : IBalloonSurface
{
    public void Show(int timeoutMs, string title, string text, ToolTipIcon icon) => shell.ShowBalloon(title, text, icon, timeoutMs);
}

/// <summary>
/// Every balloon the tray shows goes through here. Each is logged (Windows may never show it) and drawn for as long as its kind
/// calls for (<see cref="TrayBalloons.TimeoutMs"/>). Windows reports a click without saying which balloon it was, so the one
/// handler runs what the balloon on screen offered, once; a balloon that offered nothing does nothing, and a newer balloon takes
/// the place of the older one's action, so a click never runs what an earlier balloon offered.
/// </summary>
/// <param name="surface">Draws the balloon.</param>
/// <param name="log">Records what the person was told.</param>
sealed class BalloonPresenter(IBalloonSurface surface, Action<string> log)
{
    private Action? _onClick;

    /// <summary>Shows a balloon. <paramref name="onClick"/> is what a click on it does, or null when it offers nothing.</summary>
    internal void Show(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _onClick = onClick;
        log(TrayBalloons.LogLine(title, text, icon));
        surface.Show(TrayBalloons.TimeoutMs(icon), title, text, icon);
    }

    /// <summary>A click on the balloon on screen.</summary>
    internal void OnClicked()
    {
        var onClick = _onClick;
        _onClick = null;
        onClick?.Invoke();
    }
}
