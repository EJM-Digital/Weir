using System.Text;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// Why the server could not start, in plain words, for the Windows tray (<c>apps/tray/Weir.Tray/StartupNotes.cs</c> reads the
/// same text; keep the two in step): <c>startup-error.txt</c> in Weir's data folder holds a short headline on the first line and
/// the whole plain sentence after it. The tray shows it in its hover text and its balloon instead of a bare "couldn't start".
/// How far a start has got is not here: it is <see cref="StartupProgress"/>. The file is best effort (a start never fails because
/// one could not be written), and each start removes the last one's.
/// </summary>
public static class StartupNotes
{
    public const string ErrorFileName = "startup-error.txt";

    /// <summary>Removes what an earlier start left, so the tray never reads a reason that is not this start's.</summary>
    public static void ClearStale(string home) => Delete(home, ErrorFileName);

    public static void WriteError(string home, string headline, string sentence) =>
        Write(home, ErrorFileName, $"{headline}\n{sentence}");

    private static void Write(string home, string fileName, string text)
    {
        try
        {
            AtomicFileWriter.Replace(home, fileName, Encoding.UTF8.GetBytes(text));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The tray shows its general message instead.
        }
    }

    private static void Delete(string home, string fileName)
    {
        try
        {
            File.Delete(Path.Join(home, fileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Read as the last start's, which the tray tells from this one's by when it was written.
        }
    }
}
