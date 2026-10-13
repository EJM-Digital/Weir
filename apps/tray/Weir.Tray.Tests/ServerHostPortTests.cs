using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// A saved port (port.txt) that cannot be read when the tray begins leaves the port unknown. It is not "no port chosen": nothing is
/// decided, asked or saved while the file is busy. The host looks again at the start of each attempt, with the same back-off and the same
/// start limit as any other failed start, and gives up the same way. Real stand-in servers on a fake clock, as in ServerHostTests.
/// </summary>
public sealed class ServerHostPortTests
{
    private const int TestTimeoutMs = 120_000;
    private static readonly PortChoice.Resolution StillBusy = new(null, Busy: true);

    private static string PortFile(StartRig rig) => Path.Combine(rig.HomePath, PortChoice.SavedPortFileName);

    private static string CurrentPortFile(StartRig rig) => Path.Combine(rig.HomePath, ServerHost.CurrentPortFileName);

    private static async Task<StartGaveUpException> GiveUpAsync(StartRig rig, Task start)
    {
        try
        {
            await rig.DriveAsync(start);
        }
        catch (StartGaveUpException gaveUp)
        {
            return gaveUp;
        }

        throw new InvalidOperationException("The start was expected to give up and did not.");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_saved_port_that_cannot_be_read_is_waited_for_with_the_same_back_off_and_the_server_starts_once_it_can()
    {
        using var rig = new StartRig(portUnknown: true);

        // A note an earlier server left is not a reason from this start, which has run no server.
        File.WriteAllText(Path.Combine(rig.HomePath, StartupNotes.ErrorFileName), "Weir's settings are not valid\nAn earlier start's reason.");
        var port = StartRig.AnotherPort();
        var calls = 0;
        rig.Host.ResolvePort = _ => Task.FromResult(++calls <= 2 ? StillBusy : new PortChoice.Resolution(port, Busy: false));
        Assert.Equal(0, rig.Host.Port);

        await rig.StartReadyAsync();

        Assert.Equal(3, calls);
        Assert.Equal(port, rig.Host.Port);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.Single(rig.Starts);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
            rig.Delays.Where(delay => delay != ServerHealth.RetryDelay));
        Assert.Contains("Start attempt 1 failed: port.txt could not be read (it stayed busy)", rig.TrayLog, StringComparison.Ordinal);
        Assert.Contains("Trying again in 1 s (1 of 5 failures allowed within 15 min)", rig.TrayLog, StringComparison.Ordinal);
        Assert.Contains("Trying again in 2 s (2 of 5 failures allowed within 15 min)", rig.TrayLog, StringComparison.Ordinal);
        Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(CurrentPortFile(rig)));
        Assert.False(File.Exists(PortFile(rig)), "nothing is saved because a port was found");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_saved_port_that_stays_busy_ends_the_trying_like_any_start_and_starts_nothing_asks_nothing_and_saves_nothing()
    {
        using var rig = new StartRig(portUnknown: true);
        var calls = 0;
        rig.Host.ResolvePort = _ =>
        {
            calls++;
            return Task.FromResult(StillBusy);
        };

        var gaveUp = await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None));

        Assert.Equal(StartPolicy.Default.StartLimitBurst, gaveUp.Attempts);
        Assert.Equal(StartPolicy.Default.StartLimitBurst, calls);
        Assert.Contains("port.txt could not be read", gaveUp.Reason, StringComparison.Ordinal);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Equal(StartPolicy.Default.StartLimitBurst, rig.Host.GaveUpAfterAttempts);
        Assert.Equal(0, rig.Host.Port);
        Assert.Empty(rig.Starts);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            rig.Delays);
        Assert.Contains("Giving up (5 of 5 failures allowed within 15 min); Weir is not running. Restart Weir tries again.", rig.TrayLog, StringComparison.Ordinal);
        Assert.False(File.Exists(PortFile(rig)));
        Assert.False(File.Exists(CurrentPortFile(rig)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Restart_Weir_after_giving_up_on_the_port_looks_again_with_the_whole_budget()
    {
        using var rig = new StartRig(portUnknown: true);
        var port = StartRig.AnotherPort();
        var readable = false;
        rig.Host.ResolvePort = _ => Task.FromResult(readable ? new PortChoice.Resolution(port, Busy: false) : StillBusy);
        await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None));
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);

        readable = true;
        var restart = rig.Host.RestartNowAsync(CancellationToken.None);
        await rig.DriveAsync(restart, async delay =>
        {
            if (delay == ServerHealth.RetryDelay)
            {
                await rig.LatestListeningAsync();
            }
        });

        Assert.True(await restart);
        Assert.Equal(port, rig.Host.Port);
        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.Equal(0, rig.Host.GaveUpAfterAttempts);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_person_who_closes_the_first_run_dialog_is_not_asked_again_by_the_back_off()
    {
        using var rig = new StartRig(portUnknown: true);
        var calls = 0;
        rig.Host.ResolvePort = _ =>
        {
            calls++;
            return Task.FromResult(new PortChoice.Resolution(null, Busy: false));
        };

        var gaveUp = await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Equal(1, gaveUp.Attempts);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Empty(rig.Starts);

        // The tray says what happened, not "after 1 tries".
        Assert.True(rig.Host.NoPortChosen);
        Assert.Equal("No port chosen", rig.Host.GaveUpError?.Headline);
        Assert.DoesNotContain("tries", rig.Host.GaveUpError?.Headline, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Restart_Weir_after_the_port_window_was_closed_asks_again_and_forgets_that_it_was_closed()
    {
        using var rig = new StartRig(portUnknown: true);
        var port = StartRig.AnotherPort();
        var chosen = false;
        rig.Host.ResolvePort = _ => Task.FromResult(new PortChoice.Resolution(chosen ? port : null, Busy: false));
        await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None));
        Assert.True(rig.Host.NoPortChosen);

        chosen = true;
        var restart = rig.Host.RestartNowAsync(CancellationToken.None);
        await rig.DriveAsync(restart, async delay =>
        {
            if (delay == ServerHealth.RetryDelay)
            {
                await rig.LatestListeningAsync();
            }
        });

        Assert.True(await restart);
        Assert.False(rig.Host.NoPortChosen);
        Assert.Null(rig.Host.GaveUpError);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Changing_the_port_or_who_may_connect_waits_until_the_port_is_known_and_saves_nothing()
    {
        using var rig = new StartRig(portUnknown: true);

        Assert.False(await rig.Host.MoveToPortAsync(StartRig.AnotherPort(), CancellationToken.None));
        Assert.Equal(ScopeChange.Failed, await rig.Host.MoveToScopeAsync(ListenScope.OtherDevices, CancellationToken.None));

        Assert.False(File.Exists(PortFile(rig)));
        Assert.Empty(rig.Starts);
        Assert.Equal(ListenScope.ThisPcOnly, rig.Host.Scope);
    }
}
