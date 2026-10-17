using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>A free-space check before a write-heavy step.</summary>
public sealed record DiskSpaceCheck(bool Ok, string CheckedPath, double FreeMb, long RequiredMb);

/// <summary>
/// Recoverable file writes and moves for media mutations: nothing partial is ever exposed at a final path.
/// </summary>
public static partial class FileLifecycle
{
    private const int CopyChunkBytes = 8 * 1024 * 1024;
    private const long BytesPerMib = 1024 * 1024;

    /// <summary>Bytes as MiB, never negative.</summary>
    public static double BytesToMb(long sizeBytes) => Math.Max(0.0, sizeBytes / (double)BytesPerMib);

    /// <summary>The path itself when it is an existing directory, else its closest existing ancestor.</summary>
    public static string NearestExistingParent(string path)
    {
        var current = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
            {
                return current;
            }

            current = parent;
        }

        return current;
    }

    /// <summary>Whether the drive holding <paramref name="targetPath"/> has at least <paramref name="requiredMb"/> free.
    /// A requirement of zero turns the check off.</summary>
    public static DiskSpaceCheck CheckMinimumFreeDiskSpace(string targetPath, long requiredMb, Func<string, long>? freeBytes = null)
    {
        var required = Math.Max(0, requiredMb);
        var checkedPath = NearestExistingParent(targetPath);
        if (required <= 0)
        {
            return new DiskSpaceCheck(true, checkedPath, 0.0, 0);
        }

        var free = BytesToMb((freeBytes ?? AvailableFreeBytes)(checkedPath));
        return new DiskSpaceCheck(free >= required, checkedPath, free, required);
    }

    private static long AvailableFreeBytes(string path) => new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;

    /// <summary>
    /// Creates a folder to publish into and every missing folder above it, and gives the output ownership policy to each one it made,
    /// the topmost first. A folder that was already there is left as it is.
    /// </summary>
    public static void EnsureOutputDirectory(string directory, IOutputOwnership? ownership)
    {
        var missing = new Stack<string>();
        for (var folder = directory; !string.IsNullOrEmpty(folder) && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
        {
            missing.Push(folder);
        }

        Directory.CreateDirectory(directory);
        foreach (var created in missing)
        {
            ownership?.ApplyToDirectory(created);
        }
    }

    /// <summary>
    /// Copies into a hidden <c>.{name}.XXXXXXXX.partial</c> beside the destination, validates it, then atomically
    /// replaces the destination. A failed or cancelled copy, or a failed validation, leaves nothing behind.
    /// </summary>
    public static async Task SafeCopyToFinalAsync(
        string source,
        string final,
        Func<string, Task>? validateStaged = null,
        Action<long, long>? progressCallback = null,
        bool preserveMetadata = true,
        IOutputOwnership? ownership = null,
        CancellationToken cancellationToken = default)
    {
        var src = Path.GetFullPath(source);
        var directory = Path.GetDirectoryName(Path.GetFullPath(final))!;
        EnsureOutputDirectory(directory, ownership);

        var tmp = CreateTempFile(directory, "." + Path.GetFileName(final) + ".", ".partial");
        try
        {
            await CopyContentAsync(src, tmp, progressCallback, cancellationToken).ConfigureAwait(false);
            if (preserveMetadata)
            {
                File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(src));
                File.SetLastAccessTimeUtc(tmp, File.GetLastAccessTimeUtc(src));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BestEffortDelete(tmp);
            throw new FileLifecycleException($"Could not safely copy {src} to {final}: {exception.Message}", exception);
        }
        catch (OperationCanceledException)
        {
            BestEffortDelete(tmp);
            throw;
        }

        if (validateStaged is not null)
        {
            try
            {
                await validateStaged(tmp).ConfigureAwait(false);
            }
            catch
            {
                BestEffortDelete(tmp);
                throw;
            }
        }

        try
        {
            File.Move(tmp, final, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BestEffortDelete(tmp);
            throw new FileLifecycleException($"Could not safely publish the validated copy at {final}: {exception.Message}", exception);
        }

        ownership?.ApplyToFile(Path.GetFullPath(final));
    }

    /// <summary>
    /// The bytes of <paramref name="source"/> into <paramref name="destination"/>, a chunk at a time. Asynchronous, so a
    /// copy of a large file over a slow share does not hold a thread-pool thread for minutes (#716).
    /// </summary>
    /// <remarks>
    /// Not <see cref="File.Copy(string, string, bool)"/>, although it can offload a same-share copy to the server: it
    /// reports no progress, and it carries over what this copy deliberately does not (Windows attributes and alternate
    /// streams, Unix permission bits), which would change what lands in the output folder.
    /// </remarks>
    private static async Task CopyContentAsync(string source, string destination, Action<long, long>? progressCallback, CancellationToken cancellationToken)
    {
        var total = new FileInfo(source).Length;
        long copied = 0;
        var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                var buffer = new byte[Math.Min(CopyChunkBytes, Math.Max(4096, total))];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    ReportCopyProgress(progressCallback, copied, total);
                }
            }
        }
    }

    private static void ReportCopyProgress(Action<long, long>? progressCallback, long copied, long total)
    {
        if (progressCallback is null)
        {
            return;
        }

        try
        {
            progressCallback(copied, total);
        }
#pragma warning disable CA1031 // Progress is optional observability; it must never invalidate a safe copy.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The copy carries on; a reporter that fails says so in its own log.
        }
    }

    /// <summary>
    /// Validates and atomically exposes a same-volume hard link. False means the link was refused and the caller should copy. A
    /// failed validation or publication removes the link and throws, and the source is never touched.
    /// </summary>
    public static async Task<bool> TryHardlinkToFinalAsync(string source, string final, Func<string, Task>? validateStaged = null, IOutputOwnership? ownership = null)
    {
        var src = Path.GetFullPath(source);
        var directory = Path.GetDirectoryName(Path.GetFullPath(final))!;
        EnsureOutputDirectory(directory, ownership);

        var tmp = CreateTempFile(directory, "." + Path.GetFileName(final) + ".", ".link");
        BestEffortDelete(tmp);
        if (!CreateHardLink(tmp, src))
        {
            BestEffortDelete(tmp);
            return false;
        }

        if (validateStaged is not null)
        {
            try
            {
                await validateStaged(tmp).ConfigureAwait(false);
            }
            catch
            {
                BestEffortDelete(tmp);
                throw;
            }
        }

        try
        {
            File.Move(tmp, final, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BestEffortDelete(tmp);
            throw new FileLifecycleException($"Could not safely publish the validated link at {final}: {exception.Message}", exception);
        }

        // A hard link shares one inode with the source, so the ownership/mode change lands on the source's file
        // too (and its other names, if any). That is the accepted cost of this fast path.
        ownership?.ApplyToFile(Path.GetFullPath(final));
        return true;
    }

    /// <summary>
    /// Moves the staged file to a hidden <c>.{name}.XXXXXXXX.partial</c> beside the destination, then renames it onto the
    /// destination, so the destination only ever appears complete.
    /// </summary>
    /// <remarks>
    /// <see cref="File.Move(string, string, bool)"/> does not refuse to cross volumes: it copies straight to the name it is
    /// given and then deletes the source (the <c>EXDEV</c> branch on Unix; on Windows every move passes
    /// <c>MOVEFILE_COPY_ALLOWED</c>). The default work folder lives under Weir's home, which in Docker is usually another
    /// volume from the output folder, so a direct move would expose a half-written file at its final name, which Sonarr or
    /// Radarr importing from the output folder through a remote path mapping must never see. Moving to the partial first
    /// keeps any such copy under a hidden, non-video name; the last step is a rename within one directory, which is atomic
    /// on every filesystem. Same-volume finalisation is two renames and no copy.
    /// </remarks>
    public static void SafeFinalizeFile(string staged, string final, IOutputOwnership? ownership = null) =>
        SafeFinalizeFile(staged, final, ownership, File.Move);

    /// <summary><see cref="SafeFinalizeFile(string, string, IOutputOwnership?)"/> with the move made swappable for tests.</summary>
    internal static void SafeFinalizeFile(string staged, string final, IOutputOwnership? ownership, Action<string, string, bool> move)
    {
        ArgumentNullException.ThrowIfNull(move);
        var src = Path.GetFullPath(staged);
        var destination = Path.GetFullPath(final);
        var directory = Path.GetDirectoryName(destination)!;
        EnsureOutputDirectory(directory, ownership);

        var tmp = CreateTempFile(directory, "." + Path.GetFileName(destination) + ".", ".partial");
        try
        {
            try
            {
                move(src, tmp, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The staged file could not be moved (held open, say): copy it instead, and remove it once published.
                File.Copy(src, tmp, overwrite: true);
            }

            move(tmp, destination, true);
            BestEffortDelete(src);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BestEffortDelete(tmp);
            throw new FileLifecycleException($"Could not safely finalize {src} to {destination}: {exception.Message}", exception);
        }

        ownership?.ApplyToFile(destination);
    }

    /// <summary><c>tempfile.mkstemp</c>: a new file named prefix + 8 random characters + suffix, created exclusively.</summary>
    public static string CreateTempFile(string directory, string prefix, string suffix)
    {
        const string Characters = "abcdefghijklmnopqrstuvwxyz0123456789_";
        for (var attempt = 0; ; attempt++)
        {
            var path = Path.GetFullPath(Path.Combine(directory, prefix + RandomNumberGenerator.GetString(Characters, 8) + suffix));
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                }

                return path;
            }
            catch (IOException) when (attempt < 100 && File.Exists(path))
            {
                // Name taken; draw again.
            }
        }
    }

    /// <summary>Deletes a file if it can; a failure is ignored.</summary>
    public static void BestEffortDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary><c>os.link</c>. False when the platform or volume refuses.</summary>
    public static bool CreateHardLink(string link, string existing)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateHardLinkW(link, existing, IntPtr.Zero);
        }

        try
        {
            return UnixLink(existing, link) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnixLink(string existing, string link);
}
