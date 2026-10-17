using System.Security.Cryptography;

namespace Weir.Infrastructure.Runtime;

/// <summary>
/// Replaces a small file in Weir's data folder so a reader (the Windows tray watches several of these) sees the old
/// contents or the new ones, never half of either: the bytes go to a uniquely named scratch file first and are then
/// renamed into place.
/// </summary>
/// <remarks>
/// Windows refuses a rename over a file another process has open (a reader, a virus scanner), even when that process opened it for
/// sharing. The refusal passes when the other process lets go, so the rename is tried again after a moment, up to
/// <see cref="Attempts"/> times, before the refusal is let through. The tray keeps a copy of this writer
/// (apps/tray/Weir.Tray/AtomicFile.cs): keep the two in step.
/// </remarks>
internal static class AtomicFileWriter
{
    internal const int Attempts = 5;

    internal static readonly TimeSpan LookAgainAfter = TimeSpan.FromMilliseconds(20);

    internal static void Replace(string directory, string fileName, byte[] contents) =>
        Replace(directory, fileName, contents, Thread.Sleep);

    /// <param name="directory">The folder.</param>
    /// <param name="fileName">The file in it.</param>
    /// <param name="contents">The new contents.</param>
    /// <param name="pause">Waits between tries. A test replaces it.</param>
    internal static void Replace(string directory, string fileName, byte[] contents, Action<TimeSpan> pause)
    {
        var path = Path.Join(directory, fileName);
        var scratch = Path.Join(directory, $".{fileName}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp");
        try
        {
            File.WriteAllBytes(scratch, contents);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(scratch, path, overwrite: true);
                    scratch = string.Empty;
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < Attempts)
                {
                    pause(LookAgainAfter);
                }
            }
        }
        finally
        {
            if (scratch.Length > 0 && File.Exists(scratch))
            {
                File.Delete(scratch);
            }
        }
    }
}
