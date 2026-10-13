using System.Text;

namespace Weir.Infrastructure.Runtime;

/// <summary>What a look at a file that other processes replace found.</summary>
internal enum ReplacedFileState
{
    /// <summary>The file was read.</summary>
    Read,

    /// <summary>There was no file.</summary>
    Absent,

    /// <summary>The file could not be read on any look: Windows refused each open (access denied, a sharing violation).</summary>
    Busy,
}

/// <param name="State">What the looks found.</param>
/// <param name="Text">The file's text when <paramref name="State"/> is <see cref="ReplacedFileState.Read"/>, else empty.</param>
/// <param name="WrittenUtc">When the file was last written, when it was read.</param>
internal readonly record struct ReplacedFileText(ReplacedFileState State, string Text, DateTime WrittenUtc);

/// <summary>
/// Reads a small file that another process, or another thread, replaces by renaming a new file over it (<see cref="AtomicFileWriter"/>).
/// While the rename happens Windows can refuse the open as access denied or as a sharing violation, and a virus scanner holding a file
/// just written does the same. Neither says anything about the file, so a reader looks again after a moment and only then reports
/// what it found. Whoever gets <see cref="ReplacedFileState.Busy"/> back has been told "not yet" after every look: it is never a
/// reason to take the file for damaged or to change anything it controls.
/// </summary>
/// <remarks>
/// A name that is not there is not looked for again. A rename over a file leaves the name in place the whole time, so a missing name
/// means there is no file, which is how most of these files are most of the time (no update waiting, no request from the tray,
/// nothing saved yet). The pause between looks holds its thread, and a read of a file that is not there would hold one for every look:
/// on a machine with few cores, threads held that way are threads the server's other requests are missing. Code that has a last good
/// reading to stand in for a refused file asks for <c>attempts: 1</c> and never waits.
/// </remarks>
internal static class ReplacedFile
{
    internal const int Attempts = 5;

    internal static readonly TimeSpan LookAgainAfter = TimeSpan.FromMilliseconds(20);

    /// <param name="path">The file.</param>
    /// <param name="encoding">How to decode it (UTF-8 when null). A byte order mark is honoured, as <see cref="File.ReadAllText(string, Encoding)"/> does; bytes the encoding refuses throw its <see cref="DecoderFallbackException"/>, which is the file being damaged, not busy.</param>
    /// <param name="attempts">How many looks a file that is refused gets (<see cref="Attempts"/> unless the caller has something to stand in for it).</param>
    /// <param name="pause">Waits between looks. A test replaces it.</param>
    internal static ReplacedFileText ReadText(string path, Encoding? encoding = null, int attempts = Attempts, Action<TimeSpan>? pause = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        pause ??= Thread.Sleep;
        var found = ReplacedFileState.Busy;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var written = File.GetLastWriteTimeUtc(path);
                using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return new ReplacedFileText(ReplacedFileState.Read, reader.ReadToEnd(), written);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new ReplacedFileText(ReplacedFileState.Absent, string.Empty, default);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                found = ReplacedFileState.Busy;
            }

            if (attempt < attempts)
            {
                pause(LookAgainAfter);
            }
        }

        return new ReplacedFileText(found, string.Empty, default);
    }
}
