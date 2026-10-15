using Weir.Core.Artwork;
using Weir.Core.Configuration;

namespace Weir.Infrastructure.Artwork;

/// <summary>The poster images on disk, under <c>WEIR_HOME/artwork/posters</c>, one file per poster id.</summary>
public sealed class ArtworkPosterFiles
{
    private const string TemporarySuffix = ".partial";

    /// <summary>How many times a poster that is open is tried for, and how long the wait between tries is, before the old one is kept.</summary>
    internal const int ReplaceAttempts = 5;

    internal static readonly TimeSpan ReplaceLookAgainAfter = TimeSpan.FromMilliseconds(200);

    private readonly string _directory;

    public ArtworkPosterFiles(WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = Path.Join(options.WeirHome, "artwork", "posters");
    }

    /// <summary>Waits between tries. A test replaces it.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Pause { get; set; } = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    /// <summary>
    /// Store an image. It becomes visible whole or not at all, so a reader never sees half of it. An image already stored is replaced by
    /// renaming the new one over it, which Windows refuses while a request is sending the old one; the rename is tried again as the
    /// request ends. Returns false when the old image stayed open through every try: it is then kept as it was, and the caller tries again
    /// on its next pass.
    /// </summary>
    public async Task<bool> WriteAsync(string posterId, byte[] image, CancellationToken cancellationToken)
    {
        var path = PathFor(posterId);
        Directory.CreateDirectory(_directory);
        var temporary = path + TemporarySuffix;
        await File.WriteAllBytesAsync(temporary, image, cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    return true;
                }
                catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && File.Exists(path))
                {
                    if (attempt == ReplaceAttempts)
                    {
                        return false;
                    }
                }

                await Pause(ReplaceLookAgainAfter, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// The image, opened for reading, or null when it is not on disk. It is opened sharing delete, so that removing the image (<see cref="Delete"/>)
    /// while it is being sent does not fail, and opening it as a new copy is renamed into place (<see cref="WriteAsync"/>) is not refused.
    /// </summary>
    public Stream? Open(string posterId)
    {
        var path = PathFor(posterId);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true)
            : null;
    }

    /// <summary>Remove an image. An image that is already gone is not an error.</summary>
    public void Delete(string posterId) => File.Delete(PathFor(posterId));

    public bool Exists(string posterId) => File.Exists(PathFor(posterId));

    /// <summary>The file for an id. Only ids <see cref="ArtworkKeys.IsPosterId"/> accepts name a file, so no id can reach outside the folder.</summary>
    private string PathFor(string posterId) =>
        ArtworkKeys.IsPosterId(posterId)
            ? Path.Join(_directory, posterId)
            : throw new ArgumentException("Not a poster id.", nameof(posterId));
}
