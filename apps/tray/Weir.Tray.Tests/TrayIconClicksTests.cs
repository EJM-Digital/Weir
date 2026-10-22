using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>Which clicks on the tray icon open Weir: the left button and its double-click, and nothing else.</summary>
public sealed class TrayIconClicksTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<(int Port, string Path)> _browser = [];

    private TrayIconClicks Clicks() => new(new WeirOpener(
        () => ServerPhase.Running,
        () => 9400,
        new Debounce(TimeSpan.FromMilliseconds(1250), _clock),
        (port, path) =>
        {
            _browser.Add((port, path));
            return true;
        },
        (_, _, _, _) => { },
        () => { }));

    [Fact]
    public void A_left_click_opens_Weir()
    {
        Clicks().OnMouseClick(MouseButtons.Left);

        Assert.Equal([(9400, "/")], _browser);
    }

    [Theory]
    [InlineData((int)MouseButtons.Middle)]
    [InlineData((int)MouseButtons.Right)]
    [InlineData((int)MouseButtons.XButton1)]
    [InlineData((int)MouseButtons.XButton2)]
    [InlineData((int)MouseButtons.None)]
    public void Any_other_button_opens_nothing(int button)
    {
        Clicks().OnMouseClick((MouseButtons)button);

        Assert.Empty(_browser);
    }

    [Fact]
    public void A_middle_click_does_not_stop_the_next_left_click_from_opening_Weir()
    {
        var clicks = Clicks();

        clicks.OnMouseClick(MouseButtons.Middle);
        clicks.OnMouseClick(MouseButtons.Left);

        Assert.Single(_browser);
    }

    [Fact]
    public void A_double_click_opens_one_window_with_the_click_that_began_it()
    {
        var clicks = Clicks();

        clicks.OnMouseClick(MouseButtons.Left);
        _clock.Advance(TimeSpan.FromMilliseconds(120));
        clicks.OnDoubleClick();

        Assert.Single(_browser);
    }
}
