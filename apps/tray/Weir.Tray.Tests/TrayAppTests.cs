using System.Collections;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>The tray as a process: put together, begun on the thread that owns the message loop, and taken down.</summary>
public sealed class TrayAppTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempDirectory _install = TempDirectory.Create();
    private readonly Dictionary<string, string?> _environment = Snapshot();

    public void Dispose()
    {
        // Beginning the tray sets the variables every server process is started with, in this process.
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = (string)variable.Key;
            if (!_environment.ContainsKey(name))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
        foreach (var (name, value) in _environment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        _home.Dispose();
        _install.Dispose();
    }

    private static Dictionary<string, string?> Snapshot() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);

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

    // The folder the tray is installed in, holding the web app the server serves.
    private string InstallWithWebAssets()
    {
        Directory.CreateDirectory(Path.Combine(_install.Path, "web-dist"));
        File.WriteAllText(Path.Combine(_install.Path, "web-dist", "index.html"), "<html></html>");
        return _install.Path;
    }

    private static ShellEffects Effects(List<string> loop) => new(
        _ => { },
        () => false,
        (_, _) => null,
        _ => { },
        _ => { },
        () => loop.Add("run"),
        () => loop.Add("exit-thread"),
        () => loop.Add("exit"));

    [Fact]
    public void Running_begins_the_tray_saves_the_port_and_returns_the_exit_code_when_the_message_loop_ends()
    {
        OnSta(() =>
        {
            var loop = new List<string>();
            using var signal = new SecondLaunchSignal($@"Local\WeirTrayTests-{Guid.NewGuid():n}");
            using var app = new TrayApp(
                9347,
                Weir.Tray.LanAccess.ListenScope.ThisPcOnly,
                new TrayStart(AnnounceOnReady: false, Interactive: false),
                new FakeUpdateService { IsInstalled = false },
                new UpdateSettings { CheckOnStartup = false, CheckIntervalMinutes = 0 },
                signal,
                Effects(loop),
                InstallWithWebAssets());

            var exitCode = app.Run();

            Assert.Equal(0, exitCode);
            Assert.Equal(["run"], loop);
            Assert.Equal("9347", File.ReadAllText(Path.Combine(_home.Path, ServerHost.CurrentPortFileName)));
            Assert.Contains("Tray icon event loop ended.", File.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName)), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_tray_that_is_made_with_the_real_windows_effects_can_be_taken_down_without_running()
    {
        using var signal = new SecondLaunchSignal($@"Local\WeirTrayTests-{Guid.NewGuid():n}");

        using var app = new TrayApp(
            0,
            Weir.Tray.LanAccess.ListenScope.ThisPcOnly,
            new TrayStart(AnnounceOnReady: true, Interactive: true),
            new FakeUpdateService { IsInstalled = false },
            new UpdateSettings(),
            signal,
            () => new PortChoice.Resolution(9347, Busy: false));

        Assert.NotNull(app);
    }
}
