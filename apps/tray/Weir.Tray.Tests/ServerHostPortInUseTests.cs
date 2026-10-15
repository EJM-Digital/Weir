using System.Globalization;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// A server that ends the start with the exit code for "port in use" (what the real server does when another program holds its port,
/// proved against the real server in Weir.Api.Tests.PortInUseTests) is tried again like any failed start, because the other program
/// can let go; when the tries are used up the tray names the port. Real stand-in servers on a fake clock, as in ServerHostTests.
/// </summary>
public sealed class ServerHostPortInUseTests
{
    private const int TestTimeoutMs = 120_000;

    private static string PortWords(StartRig rig) => string.Create(CultureInfo.InvariantCulture, $"port {rig.Host.Port} is in use by another program");

    private static Func<TimeSpan, Task> AfterExit(StartRig rig) => async delay =>
    {
        if (delay == ServerHealth.RetryDelay)
        {
            await rig.LatestExitedAsync();
        }
    };

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_port_in_use_is_tried_again_with_the_usual_back_off_and_the_log_names_the_port()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", ServerExitCodes.PortInUse.ToString(CultureInfo.InvariantCulture));

        var start = rig.Host.StartAsync(CancellationToken.None);
        var gaveUp = await GiveUpAsync(rig, start);

        Assert.Equal(StartPolicy.Default.StartLimitBurst, gaveUp.Attempts);
        Assert.Equal(StartPolicy.Default.StartLimitBurst, rig.Starts.Count);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            rig.Delays.Where(delay => delay != ServerHealth.RetryDelay));
        Assert.Contains($"Start attempt 1 failed: exited with code {ServerExitCodes.PortInUse}", rig.TrayLog, StringComparison.Ordinal);
        Assert.Contains(PortWords(rig), rig.TrayLog, StringComparison.Ordinal);
        Assert.Contains("Trying again in 1 s (1 of 5 failures allowed within 15 min)", rig.TrayLog, StringComparison.Ordinal);
        Assert.Contains(PortWords(rig), gaveUp.Reason, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task When_the_tries_are_used_up_the_log_and_the_icon_name_the_port_and_say_what_to_do()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", ServerExitCodes.PortInUse.ToString(CultureInfo.InvariantCulture));

        await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None));

        var port = rig.Host.Port.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(ServerPhase.Stopped, rig.Host.Phase);
        Assert.Equal(rig.Host.Port, rig.Host.GaveUpPortInUse);
        Assert.Equal($"Port {port} is in use", rig.Host.GaveUpError?.Headline);
        Assert.Contains($"Close the program that is using port {port}", rig.TrayLog, StringComparison.Ordinal);
        Assert.Contains("then use Restart Weir", rig.TrayLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart Weir tries again", rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_port_the_other_program_lets_go_of_between_tries_starts_the_server_without_the_person_doing_anything()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", ServerExitCodes.PortInUse.ToString(CultureInfo.InvariantCulture));
        StartRig.Steer("WEIR_STANDIN_SUCCEED_AT_ATTEMPT", "3");

        var start = rig.Host.StartAsync(CancellationToken.None);
        await rig.DriveAsync(start, async delay =>
        {
            if (delay != ServerHealth.RetryDelay)
            {
                return;
            }

            await rig.LatestStartedAsync(1);
            await StartRig.UntilAsync(() => !StartRig.IsRunning(rig.Starts[^1].Pid) || rig.Starts[^1].Attempt == "3", "the stand-in to exit or the third attempt to start");
            if (rig.Starts[^1].Attempt == "3")
            {
                await rig.LatestListeningAsync();
            }
        });

        Assert.Equal(ServerPhase.Running, rig.Host.Phase);
        Assert.Equal(0, rig.Host.GaveUpPortInUse);
        Assert.Contains("Weir started on attempt 3, after 2 failed attempts", rig.TrayLog, StringComparison.Ordinal);
        Assert.Contains(PortWords(rig), rig.TrayLog, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Any_other_exit_code_is_not_worded_as_a_port_in_use()
    {
        using var rig = new StartRig();
        StartRig.Steer("WEIR_STANDIN_EXIT_CODE", "3");

        await GiveUpAsync(rig, rig.Host.StartAsync(CancellationToken.None));

        Assert.Equal(0, rig.Host.GaveUpPortInUse);
        Assert.Equal("Didn't start after 5 tries", rig.Host.GaveUpError?.Headline);
        Assert.DoesNotContain("in use by another program", rig.TrayLog, StringComparison.Ordinal);
    }

    private static async Task<StartGaveUpException> GiveUpAsync(StartRig rig, Task start)
    {
        try
        {
            await rig.DriveAsync(start, AfterExit(rig));
        }
        catch (StartGaveUpException gaveUp)
        {
            return gaveUp;
        }

        throw new InvalidOperationException("The start was expected to give up and did not.");
    }
}
