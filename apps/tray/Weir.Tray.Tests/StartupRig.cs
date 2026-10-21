using Weir.Tray.LanAccess;
using Weir.Tray.Tests.Firewall;

namespace Weir.Tray.Tests;

/// <summary>
/// Everything starting the tray reaches, as recorders: the data folder is a fresh temp folder, the running mark is a key of the
/// test's own, the second-launch signal and the session mutex have names of the test's own, and the firewall, the questions, the
/// update service and the tray itself are stand-ins that note that they were called. <see cref="Effects"/> is what
/// <see cref="TrayStartup"/> is given; a test changes a part with <c>with</c>.
/// </summary>
internal sealed class StartupRig : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempRegistryKey _key = TempRegistryKey.Under(@"Software\WeirTrayStartupTests");

    internal StartupRig() => Effects = Build();

    internal string Home => _home.Path;

    internal string MutexName { get; } = $@"Local\WeirTrayStartupTests-{Guid.NewGuid():n}";

    internal string SignalName { get; } = $@"Local\WeirTrayStartupSignal-{Guid.NewGuid():n}";

    internal StartupEffects Effects { get; set; }

    /// <summary>What was called, in order.</summary>
    internal List<string> Calls { get; } = [];

    internal List<Action> AtExit { get; } = [];

    internal List<PortPrompt> Prompts { get; } = [];

    internal List<string> Errors { get; } = [];

    internal List<TrayLaunch> Launches { get; } = [];

    internal List<(string Home, ListenScope Scope, bool Interactive)> Widened { get; } = [];

    internal HashSet<int> PortsInUse { get; } = [];

    internal bool Interactive { get; set; }

    internal int? ChosenPort { get; set; } = PortChoice.DefaultPort;

    internal int ExitCode { get; set; } = 7;

    internal FakeUpdateService Updates { get; set; } = new() { FindsUpdate = false };

    internal FakeFirewallPolicy Firewall { get; } = new();

    internal TrayRunningMark Mark => new(_key.Path);

    internal string Log => File.Exists(Path.Combine(Home, TrayLog.FileName)) ? File.ReadAllText(Path.Combine(Home, TrayLog.FileName)) : "";

    internal int Run(params string[] args) => new TrayStartup(Effects, MutexName).Run(args);

    public void Dispose()
    {
        _key.Dispose();
        _home.Dispose();
    }

    private StartupEffects Build() => new(
        (_, log) =>
        {
            Calls.Add("firewall-command");
            log("The firewall command ran.");
            return 42;
        },
        _ => Calls.Add("installer-hooks"),
        () => false,
        () => new SecondLaunchSignal(SignalName),
        () => Calls.Add("second-launch"),
        () => Mark,
        AtExit.Add,
        () => Calls.Add("error-log"),
        _ => Calls.Add("stdio"),
        () => Calls.Add("windows-forms"),
        _ =>
        {
            Calls.Add("update-service");
            return Updates;
        },
        () => Calls.Add("orphans"),
        _ => false,
        () => Interactive,
        port => PortsInUse.Contains(port),
        prompt =>
        {
            Prompts.Add(prompt);
            return ChosenPort;
        },
        () => Firewall,
        (home, scope, interactive) => Widened.Add((home, scope, interactive)),
        Errors.Add,
        launch =>
        {
            Launches.Add(launch);
            return ExitCode;
        });
}
