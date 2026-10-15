using Weir.Core.Artwork;
using Weir.Core.Configuration;

namespace Weir.Infrastructure.Artwork;

/// <summary>The poster images on disk, under <c>WEIR_HOME/artwork/posters</c>, one file per poster id.</summary>
public sealed class ArtworkPosterFiles
{
    private const string TemporarySuffix = ".partial";

    private readonly string _directory;

    public ArtworkPosterFiles(WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = Path.Join(options.WeirHome, "artwork", "posters");
    }

    /// <summary>Store an image. It becomes visible whole or not at all, so a reader never sees half of it.</summary>
    public async Task WriteAsync(string posterId, byte[] image, CancellationToken cancellationToken)
    {
        var path = PathFor(posterId);
        Directory.CreateDirectory(_directory);
        var temporary = path + TemporarySuffix;
        await File.WriteAllBytesAsync(temporary, image, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
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
