using System.Diagnostics;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The real notification-area icon, menu and timer, driven on a thread of its own with Windows' side-effecting calls replaced by
/// recorders: what the shell is asked to show is shown, and what the person does is handed on.
/// </summary>
public sealed class WindowsTrayShellTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly Recorder _recorder = new();

    public void Dispose() => _home.Dispose();

    private sealed class Recorder
    {
        public List<ProcessStartInfo> Started { get; } = [];

        public List<string> Clipboard { get; } = [];

        public List<string> Warned { get; } = [];

        public List<Icon?> PortIcons { get; } = [];

        public int? PortAnswer { get; set; } = 9400;

        public bool StartWithWindowsAnswer { get; set; } = true;

        public List<string> Loop { get; } = [];

        public ShellEffects Effects => new(
            Started.Add,
            () => StartWithWindowsAnswer,
            (_, icon) =>
            {
                PortIcons.Add(icon);
                return PortAnswer;
            },
            Clipboard.Add,
            Warned.Add,
            () => Loop.Add("run"),
            () => Loop.Add("exit-thread"),
            () => Loop.Add("exit"));
    }

    private sealed class Handled
    {
        public List<string> Calls { get; } = [];

        public Dictionary<TrayMenuItem, Action> Clicks { get; } = [];

        public TrayShellHandlers Handlers => new(
            Clicks,
            () => Calls.Add("menu-opening"),
            () => Calls.Add("icon-clicked"),
            () => Calls.Add("icon-double-clicked"),
            () => Calls.Add("balloon-clicked"),
            () => Calls.Add("blink-tick"));
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static MouseEventArgs Click(MouseButtons button) => new(button, 1, 0, 0, 0);

    [Fact]
    public void What_is_posted_runs_on_the_thread_that_was_attached_once_it_pumps_messages()
    {
        OnSta(() =>
        {
            var shell = new WindowsTrayShell(_recorder.Effects);
            shell.AttachToThisThread();
            var ran = new List<int> { };
            var threadId = Environment.CurrentManagedThreadId;

            shell.Post(() => ran.Add(Environment.CurrentManagedThreadId));
            Assert.Empty(ran);
            Application.DoEvents();

            Assert.Equal([threadId], ran);
        });
    }

    [Fact]
    public void Attaching_a_thread_that_already_has_a_windows_forms_context_keeps_it()
    {
        OnSta(() =>
        {
            var shell = new WindowsTrayShell(_recorder.Effects);
            shell.AttachToThisThread();
            var context = SynchronizationContext.Current;

            shell.AttachToThisThread();

            Assert.Same(context, SynchronizationContext.Current);
        });
    }

    [Fact]
    public void Opening_shows_the_icon_with_the_menu_and_a_timer_that_is_not_running()
    {
        OnSta(() =>
        {
            using var shell = new WindowsTrayShell(_recorder.Effects);

            shell.Open(new Handled().Handlers);

            Assert.True(shell.NotifyIcon!.Visible);
            Assert.NotNull(shell.NotifyIcon.Icon);
            Assert.Same(shell.Menu!.Strip, shell.NotifyIcon.ContextMenuStrip);
            Assert.False(shell.BlinkTimer!.Enabled);
            Assert.Equal((int)TrayBlink.Interval.TotalMilliseconds, shell.BlinkTimer.Interval);
        });
    }

    [Fact]
    public void The_icon_the_hover_text_and_the_menu_are_what_the_controller_asked_for()
    {
        OnSta(() =>
        {
            using var shell = new WindowsTrayShell(_recorder.Effects);
            shell.Open(new Handled().Handlers);

            shell.ShowIcon(new TrayIconKey(TrayDot.Green, TrayMark.Paused));
            shell.ShowHoverText("Weir - Running");
            shell.ShowMenu([new TrayMenuEntry(TrayMenuItem.Open, "Open Weir"), new TrayMenuEntry(TrayMenuItem.Quit, "Quit")]);

            Assert.Equal("Weir - Running", shell.NotifyIcon!.Text);
            Assert.NotNull(shell.NotifyIcon.Icon);
            Assert.Equal(["Open Weir", "Quit"], shell.Menu!.Strip.Items.OfType<ToolStripMenuItem>().Select(item => item.Text));
        });
    }

    [Fact]
    public void The_blink_timer_runs_while_the_icon_blinks_and_stops_when_it_does_not()
    {
        OnSta(() =>
        {
            using var shell = new WindowsTrayShell(_recorder.Effects);
            shell.Open(new Handled().Handlers);

            shell.SetBlinking(true);
            Assert.True(shell.BlinkTimer!.Enabled);
            shell.SetBlinking(false);

            Assert.False(shell.BlinkTimer.Enabled);
        });
    }

    [Fact]
    public void A_balloon_for_an_icon_that_is_hidden_is_not_shown_and_does_no_harm()
    {
        OnSta(() =>
        {
            using var shell = new WindowsTrayShell(_recorder.Effects);
            shell.Open(new Handled().Handlers);

            shell.Hide();
            shell.ShowBalloon("Weir", "Hello.", ToolTipIcon.Info, 10_000);

            Assert.False(shell.NotifyIcon!.Visible);
        });
    }

    [Fact]
    public void What_the_person_does_to_the_icon_is_handed_to_the_controller()
    {
        OnSta(() =>
        {
            using var shell = new WindowsTrayShell(_recorder.Effects);
            var handled = new Handled();
            shell.Open(handled.Handlers);

            shell.OnMouseClick(null, Click(MouseButtons.Left));
            shell.OnMouseClick(null, Click(MouseButtons.Right));
            shell.OnMouseClick(null, Click(MouseButtons.Middle));
            shell.OnDoubleClick(null, EventArgs.Empty);
            shell.OnBalloonTipClicked(null, EventArgs.Empty);
            shell.OnBlinkTimer(null, EventArgs.Empty);
            shell.OnMenuOpening(null, new System.ComponentModel.CancelEventArgs());

            Assert.Equal(["icon-clicked", "icon-double-clicked", "balloon-clicked", "blink-tick", "menu-opening"], handled.Calls);
        });
    }

    [Fact]
    public void A_click_on_a_menu_item_runs_what_the_controller_gave_for_it()
    {
        OnSta(() =>
        {
            using var shell = new WindowsTrayShell(_recorder.Effects);
            var handled = new Handled();
            var quit = 0;
            handled.Clicks[TrayMenuItem.Quit] = () => quit++;
            shell.Open(handled.Handlers);
            shell.ShowMenu([new TrayMenuEntry(TrayMenuItem.Quit, "Quit")]);

            shell.Menu!.Strip.Items.OfType<ToolStripMenuItem>().Single().PerformClick();

            Assert.Equal(1, quit);
        });
    }

    [Fact]
    public void The_clipboard_the_browser_and_the_folders_go_through_the_effects()
    {
        OnSta(() =>
        {
            var shell = new WindowsTrayShell(_recorder.Effects);
            var folder = Path.Combine(_home.Path, "logs");
            var file = Path.Combine(_home.Path, "tray-host.log");
            File.WriteAllText(file, "x");

            shell.CopyToClipboard("http://localhost:9347");
            var opened = shell.OpenBrowser(9347, "/system?tab=about");
            shell.OpenFolder(folder, "logs folder");
            shell.RevealFile(file);

            Assert.Equal(["http://localhost:9347"], _recorder.Clipboard);
            Assert.True(opened);
            Assert.Equal(
                ["http://127.0.0.1:9347/system?tab=about", folder, "explorer.exe"],
                _recorder.Started.Select(info => info.FileName));
            Assert.Equal(RuntimeFolders.SelectArguments(file), _recorder.Started[2].Arguments);
            Assert.True(Directory.Exists(folder));
        });
    }

    [Fact]
    public void A_folder_that_will_not_open_is_told_to_the_person_through_the_effects()
    {
        OnSta(() =>
        {
            var shell = new WindowsTrayShell(_recorder.Effects with { StartProcess = _ => throw new System.ComponentModel.Win32Exception("Explorer is not available") });
            var folder = Path.Combine(_home.Path, "logs");

            shell.OpenFolder(folder, "logs folder");

            Assert.Equal($"Weir could not open its logs folder. You can open it in File Explorer yourself:\n\n{folder}", Assert.Single(_recorder.Warned));
        });
    }

    [Fact]
    public void The_questions_are_asked_through_the_effects_and_the_port_window_carries_the_trays_icon()
    {
        OnSta(() =>
        {
            using var shell = new WindowsTrayShell(_recorder.Effects);
            var prompt = new PortPrompt(PortPromptReason.Change, 9347, false, 9347);

            var beforeOpen = shell.AskPort(prompt);
            shell.Open(new Handled().Handlers);
            var afterOpen = shell.AskPort(prompt);

            Assert.Equal(9400, beforeOpen);
            Assert.Equal(9400, afterOpen);
            Assert.Null(_recorder.PortIcons[0]);
            Assert.Same(shell.NotifyIcon!.Icon, _recorder.PortIcons[1]);
            Assert.True(shell.AskStartWithWindows());
            _recorder.StartWithWindowsAnswer = false;
            Assert.False(shell.AskStartWithWindows());
        });
    }

    [Fact]
    public void Running_and_exiting_go_through_the_effects()
    {
        var shell = new WindowsTrayShell(_recorder.Effects);

        shell.Run();
        shell.ExitThread();
        shell.Exit();

        Assert.Equal(["run", "exit-thread", "exit"], _recorder.Loop);
    }

    [Fact]
    public void A_shell_that_was_never_opened_can_be_disposed()
    {
        new WindowsTrayShell(_recorder.Effects).Dispose();
    }
}
