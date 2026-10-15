using Xunit;

namespace Weir.Tray.Tests;

/// <summary>A second launch tells the Weir that is running, and a silent one stays silent.</summary>
public sealed class SecondLaunchTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(20);

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly string _name = $@"Local\WeirTrayTests-{Guid.NewGuid():n}";
    private readonly SemaphoreSlim _heard = new(0);

    public void Dispose()
    {
        _heard.Dispose();
        _home.Dispose();
    }

    [Fact]
    public void A_second_launch_reaches_the_running_tray_each_time()
    {
        using var listening = new SecondLaunchSignal(_name);
        listening.Subscribe(() => _heard.Release());

        SecondLaunchSignal.Raise(_name);
        Assert.True(_heard.Wait(Ceiling));
        SecondLaunchSignal.Raise(_name);
        Assert.True(_heard.Wait(Ceiling));
    }

    [Fact]
    public void A_second_launch_before_the_tray_has_an_icon_is_kept_and_told_when_it_has()
    {
        using var listening = new SecondLaunchSignal(_name);

        SecondLaunchSignal.Raise(_name);
        SpinWait.SpinUntil(() => listening.HasUndelivered, Ceiling);
        listening.Subscribe(() => _heard.Release());

        Assert.True(_heard.Wait(Ceiling));
    }

    [Fact]
    public void A_second_launch_is_told_once_to_a_tray_that_subscribes_late_and_not_again()
    {
        using var listening = new SecondLaunchSignal(_name);
        SecondLaunchSignal.Raise(_name);
        SpinWait.SpinUntil(() => listening.HasUndelivered, Ceiling);
        listening.Subscribe(() => _heard.Release());
        Assert.True(_heard.Wait(Ceiling));

        listening.Subscribe(() => _heard.Release());

        // A told launch is delivered on the thread that subscribes, so it is already in the count if it was told twice. A launch
        // that follows is the proof the listener still hears new ones, and it is told once.
        Assert.Equal(0, _heard.CurrentCount);
        SecondLaunchSignal.Raise(_name);
        Assert.True(_heard.Wait(Ceiling));
        Assert.Equal(0, _heard.CurrentCount);
    }

    [Fact]
    public void With_no_tray_listening_a_second_launch_has_nobody_to_tell_and_does_no_harm()
    {
        SecondLaunchSignal.Raise(_name);
    }

    [Fact]
    public void The_running_tray_is_told_when_Weir_is_started_again_by_hand_or_at_sign_in()
    {
        foreach (var args in new[] { Array.Empty<string>(), ["--no-browser"], ["--port", "9400"] })
        {
            var told = false;

            Program.HandOverToRunningTray(args, () => told = true);

            Assert.True(told, string.Join(' ', args));
        }
    }

    [Fact]
    public void A_silent_second_launch_tells_nobody()
    {
        var told = false;

        Program.HandOverToRunningTray(["--silent"], () => told = true);
        Program.HandOverToRunningTray(["--no-browser", "--silent"], () => told = true);

        Assert.False(told);
    }
}
