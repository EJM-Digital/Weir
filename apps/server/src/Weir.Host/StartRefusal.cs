using Weir.Core.Configuration;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;

namespace Weir.Host;

/// <summary>
/// What the server does when it refuses to start for a reason that starting again cannot change: it says why on standard error and
/// leaves the same words in <c>startup-error.txt</c>, which is how the Windows tray knows not to try again and what to show the person
/// (<see cref="StartupNotes"/>). The message goes through <see cref="NonBlockingConsole"/> and the process exits whether or not
/// anything reads it, so a refusal never turns into a hang on a pipe that is full.
/// </summary>
internal static class StartRefusal
{
    /// <summary>
    /// The exit code of a server that could not listen because another program holds its port: <c>EX_TEMPFAIL</c> in BSD's sysexits (75), "a
    /// temporary failure, the user is invited to retry". Starting again can succeed once the other program lets go, so the Windows tray
    /// retries it as it retries any failed start, and it reads this code to say which port.
    /// </summary>
    internal const int PortInUseExitCode = 75;

    /// <summary>How long the process waits for its last words to reach a console that is taking them.</summary>
    internal static readonly TimeSpan Courtesy = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reports <paramref name="exception"/> and returns the exit code for it.
    /// </summary>
    internal static int Report(Exception exception, RuntimeEnvironment runtime, NonBlockingConsole error)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(error);
        var headline = exception switch
        {
            WeirConfigurationException => "Weir's settings are not valid",
            DatabaseSchemaMismatchException => "Weir can't use its database",
            _ => null,
        };
        if (headline is not null)
        {
            WriteNote(runtime, headline, exception.Message);
        }

        error.WriteLine($"Weir cannot start: {exception.Message}");
        error.Drain(Courtesy);
        return 1;
    }

    /// <summary>
    /// Reports that the server could not listen because another program holds its port, and returns <see cref="PortInUseExitCode"/>. It
    /// leaves no <c>startup-error.txt</c>: that file tells the tray that starting again cannot help, and here it can.
    /// </summary>
    internal static int ReportPortInUse(int port, NonBlockingConsole error)
    {
        ArgumentNullException.ThrowIfNull(error);
        error.WriteLine($"Weir cannot start: {PortInUse.Sentence(port)}");
        error.Drain(Courtesy);
        return PortInUseExitCode;
    }

    // Best effort, like every note: when the data folder cannot even be worked out, the tray has the exit code and its own log.
    private static void WriteNote(RuntimeEnvironment runtime, string headline, string sentence)
    {
        try
        {
            // The refusal comes before the data folder is made, on a first start.
            var home = RuntimePaths.Resolve(runtime).Home;
            Directory.CreateDirectory(home);
            StartupNotes.WriteError(home, headline, sentence);
        }
        catch (Exception exception) when (exception is WeirConfigurationException or IOException or UnauthorizedAccessException)
        {
            // Nowhere to put it.
        }
    }
}
