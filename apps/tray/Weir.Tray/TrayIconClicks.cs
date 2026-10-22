namespace Weir.Tray;

/// <summary>
/// What a click on the tray icon itself does. A person asking for Weir clicks it with the left button, so a left click opens
/// Weir (#638) and so does a double-click, which still opens one window because its second event falls inside the debounce.
/// A middle click does nothing, and a right click only shows the menu, which Windows does by itself.
/// </summary>
/// <param name="opener">Opens Weir, or says why it cannot yet.</param>
sealed class TrayIconClicks(WeirOpener opener)
{
    internal void OnMouseClick(MouseButtons button)
    {
        if (button == MouseButtons.Left)
        {
            opener.OpenFromClick("tray-click");
        }
    }

    internal void OnDoubleClick() => opener.OpenFromClick("tray-dblclick");
}
