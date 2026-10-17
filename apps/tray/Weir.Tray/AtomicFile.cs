namespace Weir.Tray;

/// <summary>
/// Saves a small settings file so a reader sees the old contents or the new ones, never half of either. The text is
/// written to a scratch file first and renamed into place; a plain <c>File.WriteAllText</c> truncates before it
/// writes, so a crash mid-write would leave the torn file every loader then has to guess its way around.
/// </summary>
static class AtomicFile
{
    internal const int Attempts = 5;

    internal static readonly TimeSpan LookAgainAfter = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Replaces <paramref name="fileName"/> in <paramref name="directory"/>, creating the folder if needed. The
    /// scratch name is unique per write, because the server writes some of these files too: a shared scratch name
    /// would let one writer rename the other's file into place. It sits in the same folder so the rename stays
    /// atomic, and starts with a dot so a leftover is not mistaken for a real file.
    /// </summary>
    /// <remarks>
    /// Windows refuses a rename over a file another process has open (a reader, a virus scanner), even when that process opened it for
    /// sharing. The refusal passes when the other process lets go, so the rename is tried again after a moment, up to
    /// <see cref="Attempts"/> times, before the refusal is let through. The server keeps a copy of this writer
    /// (apps/server/src/Weir.Infrastructure/Runtime/AtomicFileWriter.cs): keep the two in step.
    /// </remarks>
    /// <param name="directory">The folder.</param>
    /// <param name="fileName">The file in it.</param>
    /// <param name="text">The new contents.</param>
    /// <param name="pause">Waits between tries. A test replaces it.</param>
    internal static void WriteAllText(string directory, string fileName, string text, Action<TimeSpan>? pause = null)
    {
        pause ??= Thread.Sleep;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var scratch = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():n}.tmp");
        try
        {
            File.WriteAllText(scratch, text);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(scratch, path, overwrite: true);
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
            // Only still there when the write or the rename failed.
            File.Delete(scratch);
        }
    }
}
