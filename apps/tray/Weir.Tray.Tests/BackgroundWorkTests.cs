using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Nothing awaits the tray's loops and the work its menu starts, so a failure in one would vanish. BackgroundWork writes it to tray-host.log
/// with the name of what was running.
/// </summary>
public sealed class BackgroundWorkTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();

    public void Dispose() => _home.Dispose();

    private string Log => LogFile.ReadAllText(Path.Combine(_home.Path, "tray-host.log"));

    [Fact]
    public async Task A_loop_that_fails_is_logged_with_its_name_and_the_error()
    {
        await BackgroundWork.RunLoop("Periodic update check", _ => throw new InvalidOperationException("the feed answered nonsense"), CancellationToken.None);

        Assert.Contains("Periodic update check stopped after an error:", Log, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: the feed answered nonsense", Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_loop_that_ends_because_the_tray_is_shutting_down_says_so_and_is_not_an_error()
    {
        using var shutdown = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var loop = BackgroundWork.RunLoop("Update heartbeat", async token =>
        {
            started.SetResult();
            await new TaskCompletionSource().Task.WaitAsync(token);
        }, shutdown.Token);
        await started.Task;

        await shutdown.CancelAsync();
        await loop;

        Assert.Contains("Update heartbeat stopped: the tray is shutting down.", Log, StringComparison.Ordinal);
        Assert.DoesNotContain("stopped after an error", Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_loop_that_is_cancelled_when_the_tray_is_not_shutting_down_is_an_error()
    {
        await BackgroundWork.RunLoop("Update requests", _ => throw new OperationCanceledException("nobody asked"), CancellationToken.None);

        Assert.Contains("Update requests stopped after an error:", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Work_started_on_the_interface_thread_is_watched_and_a_failure_of_it_is_logged_with_its_name()
    {
        BackgroundWork.Observe("Restart Weir", Task.FromException(new IOException("the port file is locked")));

        Assert.Contains("Restart Weir stopped after an error:", Log, StringComparison.Ordinal);
        Assert.Contains("IOException: the port file is locked", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Work_that_finishes_leaves_nothing_in_the_log()
    {
        BackgroundWork.Observe("Copy address", Task.CompletedTask);

        Assert.False(File.Exists(Path.Combine(_home.Path, "tray-host.log")));
    }
}
