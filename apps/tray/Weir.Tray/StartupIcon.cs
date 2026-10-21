namespace Weir.Tray;

/// <summary>
/// The tray icon while a start-up question waits (which port to use). The standard has the icon appear at once, blinking as
/// Starting, and Weir is not running until the question is answered, so without this a start that is waiting on a window nobody
/// sees shows nothing at all. It is gone before the tray's own icon is made. A click brings the question forward.
/// </summary>
sealed class StartupIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly TrayIcons _icons;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly TrayBlink _blink = new();
    private readonly TrayState _state = new(ServerPhase.Starting, null, null, PortChoice.DefaultPort);

    internal StartupIcon(Func<Size, Icon> brandAt, Size size, Action onClick)
    {
        _icons = new TrayIcons(brandAt, size);
        _blink.Follow(_state.Dot);
        _timer = new System.Windows.Forms.Timer { Interval = (int)TrayBlink.Interval.TotalMilliseconds, Enabled = true };
        _timer.Tick += (_, _) => Tick();
        _icon = new NotifyIcon { Icon = _icons.For(_state.IconKey(_blink.Lit)), Text = _state.HoverText, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                onClick();
            }
        };
    }

    /// <summary>The icon on show right now.</summary>
    internal Icon? Current => _icon.Icon;

    /// <summary>The hover text.</summary>
    internal string Text => _icon.Text;

    /// <summary>Whether the icon is in the notification area.</summary>
    internal bool Visible => _icon.Visible;

    /// <summary>One blink: swaps to the other icon, which was made once and is kept.</summary>
    internal void Tick()
    {
        _blink.Tick();
        _icon.Icon = _icons.For(_state.IconKey(_blink.Lit));
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _icons.Dispose();
    }
}
