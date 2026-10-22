namespace Weir.Tray;

/// <summary>Why the server could not start, in the server's own plain words.</summary>
/// <param name="Headline">A few words, short enough for the icon's hover text.</param>
/// <param name="Detail">The whole sentence, with what to do about it, for a balloon.</param>
sealed record StartupError(string Headline, string Detail);

/// <summary>
/// Why a server could not start, in its own words: <c>startup-error.txt</c> in the runtime home (<c>StartupNotes</c> in the server's
/// <c>Weir.Infrastructure/Runtime</c> writes the same text; keep the two in step). A file is the server's only when it was written
/// after the server process began: the last start's is not this one's. How far a start has got is <see cref="StartProgressFile"/>.
/// </summary>
static class StartupNotes
{
    internal const string ErrorFileName = "startup-error.txt";

    /// <summary>Why the server that began at <paramref name="serverStartedUtc"/> could not start, or null when it said nothing.</summary>
    internal static StartupError? ReadError(string runtimeHome, DateTime? serverStartedUtc)
    {
        if (ReadFresh(Path.Combine(runtimeHome, ErrorFileName), serverStartedUtc) is not { } text)
        {
            return null;
        }
        var lines = text.Split('\n', 2, StringSplitOptions.TrimEntries);
        var headline = lines[0];
        var detail = lines.Length > 1 ? lines[1] : headline;
        return headline.Length == 0 ? null : new StartupError(headline, detail);
    }

    // Written no earlier than the process began: a file from an earlier process is never this one's.
    private static string? ReadFresh(string path, DateTime? serverStartedUtc)
    {
        // The server deletes the last note before it starts and writes a new one only when it refuses, so nothing replaces this file while
        // it is there and a missing one is final. A file a virus scanner or the server's own write holds for a moment is looked at again.
        var reading = ReplacedFile.ReadText(path);
        if (reading.State != ReplacedFileState.Read || (serverStartedUtc is { } started && reading.WrittenUtc < started))
        {
            return null;
        }
        return reading.Text.Trim();
    }
}
