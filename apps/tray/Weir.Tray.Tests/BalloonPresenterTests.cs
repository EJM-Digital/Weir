using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Every balloon is logged and shown for 10 seconds (information) or 30 seconds (a problem); a click runs the action the balloon on
/// screen offered, once; a balloon that offered nothing does nothing; a newer balloon replaces the older one's action.
/// </summary>
public sealed class BalloonPresenterTests
{
    private readonly RecordedBalloons _surface = new();
    private readonly List<string> _log = [];

    private BalloonPresenter Presenter() => new(_surface, _log.Add);

    private sealed class RecordedBalloons : IBalloonSurface
    {
        internal List<(int TimeoutMs, string Title, string Text, ToolTipIcon Icon)> Shown { get; } = [];

        public void Show(int timeoutMs, string title, string text, ToolTipIcon icon) => Shown.Add((timeoutMs, title, text, icon));
    }

    [Theory]
    [InlineData((int)ToolTipIcon.Info, 10_000)]
    [InlineData((int)ToolTipIcon.None, 10_000)]
    [InlineData((int)ToolTipIcon.Warning, 30_000)]
    [InlineData((int)ToolTipIcon.Error, 30_000)]
    public void A_balloon_is_shown_for_as_long_as_its_kind_calls_for(int icon, int expectedMs)
    {
        Presenter().Show("Weir", "Hello", (ToolTipIcon)icon);

        Assert.Equal([(expectedMs, "Weir", "Hello", (ToolTipIcon)icon)], _surface.Shown);
    }

    [Fact]
    public void A_balloon_is_logged_with_the_words_the_person_was_shown()
    {
        Presenter().Show("Weir Update", "Version 3.4.0 is available.", ToolTipIcon.Info);

        Assert.Equal(["Balloon (info) Weir Update: Version 3.4.0 is available."], _log);
    }

    [Fact]
    public void A_click_runs_what_the_balloon_offered_once()
    {
        var presenter = Presenter();
        var runs = 0;
        presenter.Show("Weir", "Click to open it.", ToolTipIcon.Info, () => runs++);

        presenter.OnClicked();
        presenter.OnClicked();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_click_on_a_balloon_that_offered_nothing_does_nothing()
    {
        var presenter = Presenter();
        presenter.Show("Weir", "Weir is still starting.", ToolTipIcon.Info);

        presenter.OnClicked();

        Assert.Single(_surface.Shown);
    }

    [Fact]
    public void A_click_before_any_balloon_does_nothing()
    {
        Presenter().OnClicked();

        Assert.Empty(_surface.Shown);
    }

    [Fact]
    public void A_newer_balloon_replaces_the_older_ones_action()
    {
        var presenter = Presenter();
        var restarts = 0;
        presenter.Show("Weir", "Weir stopped. Click to restart it.", ToolTipIcon.Error, () => restarts++);
        presenter.Show("Weir", "Weir is now at http://localhost:9400.", ToolTipIcon.Info);

        presenter.OnClicked();

        Assert.Equal(0, restarts);
    }

    [Fact]
    public void A_newer_balloon_that_offers_an_action_is_the_one_a_click_runs()
    {
        var presenter = Presenter();
        var ran = new List<string>();
        presenter.Show("Weir", "First", ToolTipIcon.Info, () => ran.Add("first"));
        presenter.Show("Weir", "Second", ToolTipIcon.Info, () => ran.Add("second"));

        presenter.OnClicked();

        Assert.Equal(["second"], ran);
    }

    [Fact]
    public void A_balloon_shown_from_a_click_can_offer_its_own_action()
    {
        var presenter = Presenter();
        var ran = new List<string>();
        presenter.Show("Weir", "First", ToolTipIcon.Info, () =>
        {
            ran.Add("first");
            presenter.Show("Weir", "Second", ToolTipIcon.Info, () => ran.Add("second"));
        });

        presenter.OnClicked();
        presenter.OnClicked();

        Assert.Equal(["first", "second"], ran);
    }
}
