namespace Weir.Infrastructure.IO;

/// <summary>
/// The few file-system calls Weir's sweeps make against folders other programs can change while Weir looks at them.
/// A test substitutes an implementation that fails a chosen call with the exception the operating system gives, so
/// the handling of a file that vanishes, is locked or is refused can be proved without racing a real disk.
/// </summary>
/// <remarks>Failures are <see cref="IOException"/> (including <see cref="FileNotFoundException"/> and <see cref="DirectoryNotFoundException"/>) or <see cref="UnauthorizedAccessException"/>.</remarks>
public interface IFileSystem
{
    bool DirectoryExists(string path);

    /// <summary>The files under <paramref name="path"/>, all listed before the first is returned.</summary>
    IReadOnlyList<string> ListFiles(string path, EnumerationOptions options);

    DateTime GetLastWriteTimeUtc(string path);

    void DeleteFile(string path);
}

/// <summary>The real file system.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public static PhysicalFileSystem Instance { get; } = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> ListFiles(string path, EnumerationOptions options) => [.. Directory.EnumerateFiles(path, "*", options)];

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public void DeleteFile(string path) => File.Delete(path);
}
