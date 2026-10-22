using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The first start of the tray by a person at the desktop, in order: the window that asks for a port (with Weir's own port filled in),
/// the question "Start Weir when you sign in to Windows?", and, once the server is ready, a notice that Weir is running. Nothing in the
/// start opens the browser; only a click on the notice does. These run against a throw-away key under HKCU, never the real Run key.
/// </summary>
public sealed class FirstRunTests : IDisposable
{
    private const string Executable = @"C:\Users\someone\AppData\Local\Weir\current\Weir.exe";

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempDirectory _startupFolder = TempDirectory.Create();
    private readonly TempRegistryKey _run = TempRegistryKey.Under(@"Software\WeirTrayTests");
    private readonly TempRegistryKey _answer = TempRegistryKey.Under(@"Software\WeirTrayTests");
    private readonly FakeTimeProvider _clock = new();
    private readonly List<string> _steps = [];
    private readonly List<(int Port, string Path)> _browser = [];
    private readonly List<(string Title, string Text, ToolTipIcon Icon, Action? OnClick)> _balloons = [];

    public void Dispose()
    {
        _answer.Dispose();
        _run.Dispose();
        _startupFolder.Dispose();
        _home.Dispose();
    }

    private StartupRegistration Registration() =>
        new(_run.Path, new StartWithWindowsAnswer(_answer.Path), _startupFolder.Path, Executable);

    private WeirOpener Opener(int port) => new(
        () => ServerPhase.Running,
        () => port,
        new Debounce(TimeSpan.FromMilliseconds(1250), _clock),
        (openPort, path) =>
        {
            _browser.Add((openPort, path));
            return true;
        },
        (title, text, icon, onClick) => _balloons.Add((title, text, icon, onClick)),
        () => { });

    [Fact]
    public void A_first_start_asks_for_a_port_then_asks_about_starting_with_Windows_then_says_Weir_is_running_and_opens_nothing()
    {
        string[] args = [];
        var registration = Registration();

        // The window that asks for a port, with Weir's own port filled in, and the person accepts it.
        PortPrompt? prompt = null;
        var decision = PortChoice.Decide(
            PortChoice.SuppliedPort(args, _ => null),
            saved: null,
            interactive: Program.HasInteractiveDesktop(args, () => true),
            _ => false,
            asked =>
            {
                _steps.Add("port window");
                prompt = asked;
                return asked.Suggested;
            });

        // The question, once the icon is up.
        StartWithWindowsPrompt.AskOnFirstRun(args, () => true, () =>
        {
            _steps.Add(StartWithWindowsPrompt.Question);
            return true;
        }, registration);

        // The server is ready, and the person started Weir themselves.
        Assert.True(Program.AnnouncesStart(args));
        var opener = Opener(decision.Port!.Value);
        opener.ShowRunning();
        _steps.Add("notice");

        Assert.Equal(["port window", "Start Weir when you sign in to Windows?", "notice"], _steps);
        Assert.Equal(new PortPrompt(PortPromptReason.FirstRun, PortChoice.DefaultPort, false, PortChoice.DefaultPort), prompt);
        Assert.True(decision.Save);
        Assert.True(registration.IsEnabled);
        var notice = Assert.Single(_balloons);
        Assert.Equal(("Weir", "Weir is running at http://localhost:9347. Click to open it.", ToolTipIcon.Info), (notice.Title, notice.Text, notice.Icon));
        Assert.Empty(_browser);

        // Only a click on the notice opens Weir.
        notice.OnClick!();
        Assert.Equal([(9347, "/")], _browser);
    }

    [Fact]
    public void A_person_who_says_no_to_starting_with_Windows_is_not_signed_up_for_it()
    {
        var registration = Registration();

        StartWithWindowsPrompt.AskOnFirstRun([], () => true, () => false, registration);

        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void A_silent_start_asks_nothing_and_says_nothing()
    {
        var registration = Registration();

        StartWithWindowsPrompt.AskOnFirstRun(["--silent"], () => true, () => throw new Xunit.Sdk.XunitException("The question was asked."), registration);

        Assert.False(Program.AnnouncesStart(["--silent"]));
        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void A_start_with_no_desktop_to_ask_on_asks_nothing()
    {
        var registration = Registration();

        StartWithWindowsPrompt.AskOnFirstRun([], () => false, () => throw new Xunit.Sdk.XunitException("The question was asked."), registration);

        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void A_start_that_was_not_the_persons_own_says_nothing_about_Weir_running()
    {
        Assert.False(Program.AnnouncesStart(["--no-browser"]));
        Assert.True(Program.AnnouncesStart([]));
    }
}
