using System.Text;

namespace Weir.Infrastructure.Runtime;

/// <summary>What a look at a file that other processes replace found.</summary>
internal enum ReplacedFileState
{
    /// <summary>The file was read.</summary>
    Read,

    /// <summary>There was no file, on every look.</summary>
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
/// While the rename happens Windows can refuse the open as access denied or as a sharing violation, and the name can be missing for an
/// instant; a virus scanner holding a file just written does the same. None of these says anything about the file, so a reader looks
/// again after a moment and only then reports what it found. Whoever gets <see cref="ReplacedFileState.Busy"/> or
/// <see cref="ReplacedFileState.Absent"/> back has been told "not yet" or "not there" after every look: <see cref="ReplacedFileState.Busy"/>
/// is never a reason to take the file for damaged or to change anything it controls.
/// </summary>
internal static class ReplacedFile
{
    internal const int Attempts = 5;

    internal static readonly TimeSpan LookAgainAfter = TimeSpan.FromMilliseconds(20);

    /// <param name="path">The file.</param>
    /// <param name="encoding">How to decode it (UTF-8 when null). A byte order mark is honoured, as <see cref="File.ReadAllText(string, Encoding)"/> does; bytes the encoding refuses throw its <see cref="DecoderFallbackException"/>, which is the file being damaged, not busy.</param>
    /// <param name="absentIsFinal">True for a file that nothing replaces while it exists (it is deleted first), so a missing file is not looked for again.</param>
    /// <param name="pause">Waits between looks. A test replaces it.</param>
    internal static ReplacedFileText ReadText(string path, Encoding? encoding = null, bool absentIsFinal = false, Action<TimeSpan>? pause = null)
    {
        pause ??= Thread.Sleep;
        var found = ReplacedFileState.Absent;
        for (var attempt = 1; attempt <= Attempts; attempt++)
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
                found = ReplacedFileState.Absent;
                if (absentIsFinal)
                {
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                found = ReplacedFileState.Busy;
            }

            if (attempt < Attempts)
            {
                pause(LookAgainAfter);
            }
        }

        return new ReplacedFileText(found, string.Empty, default);
    }
}
