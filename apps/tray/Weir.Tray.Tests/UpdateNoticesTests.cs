using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The balloons about an update, by the mode the person chose: Automatic says it installs by itself, Download only restarts to install
/// on a click, Notify only opens System › About on a click; and a second install asked for while one is under way is told so.
/// </summary>
public sealed class UpdateNoticesTests
{
    private readonly List<string> _clicked = [];

    private UpdateNotice Notice(UpdateMode mode) =>
        UpdateNotices.For(mode, "3.4.0", restartToUpdate: () => _clicked.Add("restart"), openAbout: () => _clicked.Add("about"));

    [Fact]
    public void Automatic_says_the_update_installs_by_itself_after_five_minutes_idle_and_offers_no_click()
    {
        var notice = Notice(UpdateMode.Auto);

        Assert.Equal("Weir Update Ready", notice.Title);
        Assert.Equal(
            "Version 3.4.0 has been downloaded. Weir installs it and restarts by itself once it has been idle for 5 minutes, or when Weir next quits or starts.",
            notice.Text);
        Assert.Null(notice.OnClick);
    }

    [Fact]
    public void Download_only_says_a_click_restarts_to_install_and_a_click_does_that()
    {
        var notice = Notice(UpdateMode.DownloadOnly);

        Assert.Equal("Weir Update Ready", notice.Title);
        Assert.Equal(
            "Version 3.4.0 has been downloaded. Click here to restart and install it, or it installs the next time Weir quits or starts.",
            notice.Text);
        notice.OnClick!();
        Assert.Equal(["restart"], _clicked);
    }

    [Fact]
    public void Notify_only_says_to_open_System_About_and_a_click_opens_it()
    {
        var notice = Notice(UpdateMode.NotifyOnly);

        Assert.Equal("Weir Update", notice.Title);
        Assert.Equal("Version 3.4.0 is available. Open System › About in Weir to update.", notice.Text);
        notice.OnClick!();
        Assert.Equal(["about"], _clicked);
    }

    [Theory]
    [InlineData((int)UpdateMode.Auto, "Notifying user: update v3.4.0 downloaded; it installs once Weir has been idle for 5 minutes, or when Weir next quits or starts.")]
    [InlineData((int)UpdateMode.DownloadOnly, "Notifying user: update v3.4.0 downloaded and ready to install.")]
    [InlineData((int)UpdateMode.NotifyOnly, "Notifying user: update v3.4.0 available.")]
    public void Each_notice_is_logged_in_its_own_words(int mode, string expected)
    {
        Assert.Equal(expected, Notice((UpdateMode)mode).Log);
    }

    private sealed class Restart(bool downloaded, bool started)
    {
        internal List<string> Log { get; } = [];

        internal List<(string Title, string Text, ToolTipIcon Icon, Action? OnClick)> Balloons { get; } = [];

        internal int Restarts { get; private set; }

        internal Task RunAsync() => new UpdateRestart(
            () => downloaded,
            () =>
            {
                Restarts++;
                return Task.FromResult(started);
            },
            (title, text, icon, onClick) => Balloons.Add((title, text, icon, onClick)),
            Log.Add).RunAsync();
    }

    [Fact]
    public async Task A_restart_with_no_update_downloaded_does_nothing()
    {
        var restart = new Restart(downloaded: false, started: true);

        await restart.RunAsync();

        Assert.Equal(0, restart.Restarts);
        Assert.Empty(restart.Balloons);
        Assert.Empty(restart.Log);
    }

    [Fact]
    public async Task A_restart_that_starts_the_install_says_nothing_more()
    {
        var restart = new Restart(downloaded: true, started: true);

        await restart.RunAsync();

        Assert.Equal(1, restart.Restarts);
        Assert.Equal(["Applying the downloaded update and restarting."], restart.Log);
        Assert.Empty(restart.Balloons);
    }

    [Fact]
    public async Task A_restart_asked_for_while_an_install_is_under_way_says_so_and_offers_no_click()
    {
        var restart = new Restart(downloaded: true, started: false);

        await restart.RunAsync();

        var balloon = Assert.Single(restart.Balloons);
        Assert.Equal(("Weir Update", "An update is already being installed.", ToolTipIcon.Info), (balloon.Title, balloon.Text, balloon.Icon));
        Assert.Null(balloon.OnClick);
    }
}
