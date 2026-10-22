namespace Weir.Tray;

/// <summary>
/// What the tray does with an error nobody handled. The tray has no console and no window to show one in, so every such error is
/// written to tray-host.log (WinForms' crash dialog or a silent exit would lose it), and the tray keeps running after the ones
/// that leave it able to: an error on the UI thread or in a task nobody awaited. An error that reaches the process itself is logged
/// before the process ends, and a start that fails is logged and ends with exit code 1 (<see cref="RunOrFail"/>).
/// </summary>
static class TrayFailureLog
{
    /// <summary>The exit code of a start that failed.</summary>
    internal const int StartFailedExitCode = 1;

    /// <summary>Wires the three kinds of unhandled error to <paramref name="log"/>.</summary>
    internal static void Install(Action<string> log)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogUiThreadError(e, log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogProcessError(e, log);
        TaskScheduler.UnobservedTaskException += (_, e) => LogTaskError(e, log);
    }

    /// <summary>Runs the tray; a start-up error is logged and ends it with <see cref="StartFailedExitCode"/>.</summary>
    internal static int RunOrFail(Func<int> run, Action<string> log)
    {
        try
        {
            return run();
        }
        catch (Exception ex)
        {
            // The last boundary before the process ends. The tray has no console, so whatever start-up threw is
            // logged here or not at all.
            log($"Fatal startup error:\n{ex}");
            return StartFailedExitCode;
        }
    }

    internal static void LogUiThreadError(ThreadExceptionEventArgs e, Action<string> log) =>
        log($"Unhandled error on the tray's UI thread:\n{e.Exception}");

    internal static void LogProcessError(UnhandledExceptionEventArgs e, Action<string> log) =>
        log($"Unhandled error; the tray is stopping:\n{e.ExceptionObject}");

    internal static void LogTaskError(UnobservedTaskExceptionEventArgs e, Action<string> log)
    {
        log($"Unobserved background error:\n{e.Exception}");
        e.SetObserved();
    }
}
