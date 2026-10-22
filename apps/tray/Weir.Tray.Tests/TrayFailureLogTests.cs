using System.Runtime.CompilerServices;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// An error nobody handled is written to tray-host.log, and the tray keeps running after one on its UI thread or in a task nobody
/// awaited; an error that reaches the process is logged before it ends, and a start that fails is logged and exits 1.
/// </summary>
public sealed class TrayFailureLogTests
{
    private readonly List<string> _log = [];

    [Fact]
    public void An_error_on_the_UI_thread_is_logged_and_the_message_loop_goes_on()
    {
        var boom = new InvalidOperationException("a menu action failed");
        var afterwards = false;

        RunOnUiThread(() =>
        {
            TrayFailureLog.Install(_log.Add);
            using var window = new Form();
            _ = window.Handle;
            window.BeginInvoke(() => throw boom);
            window.BeginInvoke(() => afterwards = true);
            Application.DoEvents();
        });

        Assert.Contains(_log, line => line.StartsWith("Unhandled error on the tray's UI thread:\n", StringComparison.Ordinal) && line.Contains(boom.Message, StringComparison.Ordinal));
        Assert.True(afterwards, "the message loop stopped after the error");
    }

    [Fact]
    public void An_error_in_a_task_nobody_awaited_is_logged_and_observed()
    {
        const string Message = "a background loop failed and nobody awaited it";
        TrayFailureLog.Install(_log.Add);

        FaultAndForget(Message);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Contains(_log, line => line.StartsWith("Unobserved background error:\n", StringComparison.Ordinal) && line.Contains(Message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_task_error_is_marked_observed_so_it_cannot_end_the_process()
    {
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("failed")));

        TrayFailureLog.LogTaskError(args, _log.Add);

        Assert.True(args.Observed);
    }

    [Fact]
    public void An_error_that_reaches_the_process_is_logged_before_the_tray_stops()
    {
        TrayFailureLog.LogProcessError(new UnhandledExceptionEventArgs(new InvalidOperationException("the process failed"), isTerminating: true), _log.Add);

        var line = Assert.Single(_log);
        Assert.StartsWith("Unhandled error; the tray is stopping:\n", line, StringComparison.Ordinal);
        Assert.Contains("the process failed", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_that_ends_normally_returns_its_exit_code()
    {
        Assert.Equal(0, TrayFailureLog.RunOrFail(() => 0, _log.Add));
        Assert.Equal(7, TrayFailureLog.RunOrFail(() => 7, _log.Add));
        Assert.Empty(_log);
    }

    [Fact]
    public void A_start_that_throws_is_logged_with_the_error_and_exits_with_code_1()
    {
        var exitCode = TrayFailureLog.RunOrFail(() => throw new FileNotFoundException("WeirServer.exe is missing"), _log.Add);

        Assert.Equal(1, exitCode);
        var line = Assert.Single(_log);
        Assert.StartsWith("Fatal startup error:\n", line, StringComparison.Ordinal);
        Assert.Contains("WeirServer.exe is missing", line, StringComparison.Ordinal);
    }

    // A message loop needs a single-threaded apartment of its own, made before any window on it.
    private static void RunOnUiThread(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
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
            throw new InvalidOperationException("The UI thread failed.", failure);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FaultAndForget(string message) => _ = Task.FromException(new InvalidOperationException(message));
}
