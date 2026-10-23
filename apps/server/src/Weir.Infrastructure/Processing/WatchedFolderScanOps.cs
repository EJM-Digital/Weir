using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>
/// Filesystem helpers and duplicate guards for watched-folder remux scan dispatch: the disk-touching half of the scan rules
/// (#537) that look at one file, its completed output or its library's folders.
/// </summary>
public static class WatchedFolderScanOps
{
    /// <summary>The file's path relative to the watched folder, with forward slashes.</summary>
    public static string RelativePosixPathUnderWatched(string watchedRoot, string filePath) =>
        Path.GetRelativePath(Path.GetFullPath(watchedRoot), Path.GetFullPath(filePath)).Replace('\\', '/');

    private static bool ExistingCompletedOutputPathIsSafe(string path, IFileSystem files) =>
        files.FileExists(path) && files.GetLength(path) > 0;

    private static string? ExpectedOutputFileForRelativePath(string outputRoot, string relativePosix)
    {
        var root = Path.GetFullPath(outputRoot);
        var parts = relativePosix.Split('/').Where(p => p.Length > 0 && p is not ("." or "..")).ToArray();
        if (parts.Length == 0)
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine([root, .. parts]));
        var relative = Path.GetRelativePath(root, candidate);
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : candidate;
    }

    /// <summary>Whether a completed remux pass for this file left an output that is still on disk. When
    /// <paramref name="sourcePath"/> is given, the completion record must also match the current source.</summary>
    public static async Task<bool> CompletedRemuxOutputExistsForRelativePathAsync(
        UnitOfWork uow,
        string relativePosix,
        string mediaScope,
        long? libraryId,
        string? outputRoot,
        string? sourcePath,
        IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        files ??= PhysicalFileSystem.Instance;
        var wantScope = ProcessingMediaScopes.Normalize(mediaScope);
        // relative_path is indexed and holds the detail's own path as the activity writer stored it (#708). The unary plus keeps
        // SQLite from choosing an index on module or event type instead, which would read every completion Weir ever recorded.
        // The path inside each detail is still compared exactly below.
        var column = ActivityClassifier.RelativePathColumn(relativePosix);
        List<string?> rows = column is null
            ? []
            : await uow.QueryAsync(
                "SELECT detail FROM activity_events WHERE relative_path = @path AND +module = @module AND +event_type = @type " +
                "ORDER BY id DESC LIMIT 50",
                reader => reader.IsDBNull(0) ? null : reader.GetString(0),
                ("@path", column),
                ("@module", "processing"),
                ("@type", ActivityEventTypes.ProcessingFileRemuxPassCompleted)).ConfigureAwait(false);

        foreach (var raw in rows)
        {
            var trimmed = (raw ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            WireValue data;
            try
            {
                data = WireJsonParser.Parse(trimmed);
            }
            catch (WireJsonDecodeException)
            {
                continue;
            }

            if (data is not WireObject dict)
            {
                continue;
            }

            if (dict.Get("ok") is not WireBool { Value: true } || dict.Get("source_deleted_after_success") is not WireBool { Value: false })
            {
                continue;
            }

            if (dict.Get("relative_media_path") is not WireString relStr || relStr.Value != relativePosix)
            {
                continue;
            }

            if (libraryId is { } wantLib && dict.Get("library_id") is WireInteger eventLib && (long)eventLib.Value != wantLib)
            {
                continue;
            }

            var jobScope = dict.Get("media_scope") is WireString scopeStr ? ProcessingMediaScopes.Normalize(scopeStr.Value) : ProcessingMediaScopes.Movie;
            if (jobScope != wantScope)
            {
                continue;
            }

            if (sourcePath is not null && !CompletedEventMatchesCurrentSource(dict, sourcePath, files))
            {
                continue;
            }

            if (dict.Get("output_file") is not WireString outputStr || outputStr.Value.Trim().Length == 0)
            {
                continue;
            }

            if (ExistingCompletedOutputPathIsSafe(outputStr.Value, files))
            {
                return true;
            }
        }

        // An output file by itself is enough to prevent an accidental duplicate remux, but not enough
        // evidence to delete a source that may have been replaced since the history row expired.
        // Cleanup callers provide sourcePath and therefore require a matching completion record above.
        if (outputRoot is not null && sourcePath is null)
        {
            var expected = ExpectedOutputFileForRelativePath(outputRoot, relativePosix);
            if (expected is not null && ExistingCompletedOutputPathIsSafe(expected, files))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CompletedEventMatchesCurrentSource(WireObject data, string sourcePath, IFileSystem files)
    {
        if (!files.FileExists(sourcePath))
        {
            return false;
        }

        // There is no inode/device fingerprint on Windows, so match on the path and size every
        // completion record carries.
        if (data.Get("inspected_source_path") is WireString inspected && data.Get("source_size_bytes") is WireInteger recordedSize)
        {
            try
            {
                var samePath = string.Equals(Path.GetFullPath(inspected.Value), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase);
                return samePath && recordedSize.Value == files.GetLength(sourcePath);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Retries removing a completed movie's source. Only the file's own folder under the watched root is removed, never
    /// the watched root itself, and only when it holds no other video (<see cref="ReleaseFolderRemoval"/>); otherwise
    /// just the file goes. <c>FolderRemoved</c> says which happened. A workflow linked to a media manager (<paramref name="links"/>)
    /// never has its source removed.
    /// </summary>
    public static (bool Ok, bool FolderRemoved, string? Reason) RetryCompletedMovieSourceCleanup(string watchedRoot, string filePath, string? mediaExtensionsCsv, WorkflowManagerLinks links)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (links.KeepsOriginals)
        {
            // The original belongs to the download client and the manager, so nothing here ever removes it.
            return (false, false, links.KeptOriginalReason);
        }

        string root, src;
        try
        {
            root = Path.GetFullPath(watchedRoot);
            src = Path.GetFullPath(filePath);
        }
        catch (ArgumentException exception)
        {
            return (false, false, $"Source cleanup retry skipped because the path was not safely under the watched folder ({exception.Message}).");
        }

        if (!PathContainment.IsUnder(root, src))
        {
            return (false, false, "Source cleanup retry skipped because the path was not safely under the watched folder.");
        }

        // The file is strictly inside the root, so its folder is either the root itself or a release folder inside it.
        var movieFolder = Path.GetDirectoryName(src)!;
        if (!PathContainment.IsUnder(root, movieFolder))
        {
            return (false, false, "Source cleanup retry skipped because the file sits directly in the watched folder root.");
        }

        try
        {
            var removal = ReleaseFolderRemoval.Remove(root, src, mediaExtensionsCsv);
            return (removal.FileRemoved, removal.FolderRemoved, removal.Reason);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CleanupFailure(exception);
        }
    }

    /// <summary>A release folder that is already gone is a finished cleanup; any other failure to remove it is a lock or block to try again.</summary>
    internal static (bool Ok, bool FolderRemoved, string? Reason) CleanupFailure(Exception exception) => exception is DirectoryNotFoundException
        ? (true, true, null)
        : (false, false, $"Source cleanup retry could not remove the release folder because this path is still locked or blocked ({exception.Message}).");

    /// <summary>
    /// Why the source cannot be opened for reading, or null when it can. The read uses .NET's file-share enforcement (the
    /// Win32 <c>CreateFileW(FILE_SHARE_READ|FILE_SHARE_DELETE)</c> probe on Windows). There is no POSIX <c>flock</c> check: it
    /// is advisory and does not catch a writer that never calls <c>flock</c> itself.
    /// </summary>
    public static string? SourceReadProblem(string filePath, IFileSystem? files = null)
    {
        try
        {
            using var handle = (files ?? PhysicalFileSystem.Instance).OpenFile(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var buffer = new byte[1];
            _ = handle.Read(buffer, 0, 1);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Weir could not open this file for reading — it is usually locked by whatever is still writing it. The system reported: {exception.Message}.";
        }
    }

    /// <summary>Resolved folders for one scan.</summary>
    public sealed record ProcessingScanPathRuntime(string WatchedFolder, string OutputFolder, string WorkFolderEffective);

    /// <summary>
    /// Resolves one library's folders, or says why they
    /// cannot be used. Reads <c>libraries</c> directly (ADR-0014); no environment path fallback.
    /// </summary>
    public static (ProcessingScanPathRuntime? Runtime, string? Error) ResolvePathRuntimeForLibrary(ProcessingLibraryRecord library, string weirHome, IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        files ??= PhysicalFileSystem.Instance;
        var label = library.Name.Trim().Length > 0 ? library.Name.Trim() : (library.MediaType == ProcessingMediaScopes.Tv ? "TV" : "Movies");

        var watchedRaw = library.WatchedFolder.Trim();
        if (watchedRaw.Length == 0)
        {
            return (null, $"The {label} workflow has no watched folder set. Manual remux and folder-scan jobs need a watched folder to resolve relative paths. Set it in Setup › Workflows before enqueueing or running those jobs.");
        }

        var watchedPath = ProcessingLibraryFolders.ExpandForFilesystem(watchedRaw);
        if (!files.DirectoryExists(watchedPath))
        {
            return (null, $"The {label} workflow's watched folder must be an existing directory.");
        }

        var folderRow = new ProcessingLibraryFolderRow(library.Id, library.MediaType, (int)library.DisplayOrder, library.WorkFolder, library.OutputFolder);
        var workRaw = library.WorkFolder.Trim();
        var workEffectiveRaw = ProcessingLibraryFolders.EffectiveWorkFolder(folderRow, weirHome);
        var workIsDefault = !string.Equals(workEffectiveRaw, workRaw, StringComparison.Ordinal);
        var workPath = ProcessingLibraryFolders.ExpandForFilesystem(workEffectiveRaw);

        var outRaw = library.OutputFolder.Trim();
        if (outRaw.Length == 0)
        {
            return (null, $"The {label} workflow has no output folder set. Set it in Setup › Workflows before running a live remux pass.");
        }

        var outputPath = ProcessingLibraryFolders.ExpandForFilesystem(outRaw);
        if (!files.DirectoryExists(outputPath))
        {
            return (null, $"The {label} workflow's output folder must be an existing directory.");
        }

        var separationError = ValidatePathSeparation(watchedPath, workPath, outputPath);
        if (separationError is not null)
        {
            return (null, separationError);
        }

        if (!workIsDefault && !files.DirectoryExists(workPath))
        {
            return (null, $"The {label} workflow's work/temp folder must be an existing directory when set to a custom path.");
        }

        return (new ProcessingScanPathRuntime(watchedPath, outputPath, workPath), null);
    }

    private static bool IsSameOrNested(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var aWithSep = a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var bWithSep = b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return b.StartsWith(aWithSep, StringComparison.OrdinalIgnoreCase) || a.StartsWith(bWithSep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why the watched, work and output folders cannot be used together, or null when none overlaps another.</summary>
    private static string? ValidatePathSeparation(string watched, string work, string output)
    {
        if (IsSameOrNested(work, output))
        {
            return "The work/temp folder and output folder must be separate (no overlap or containment).";
        }

        if (IsSameOrNested(watched, output))
        {
            return "The watched folder and output folder must be separate (no overlap or containment).";
        }

        if (IsSameOrNested(watched, work))
        {
            return "The watched folder and work/temp folder must be separate (no overlap or containment).";
        }

        return null;
    }

    /// <summary>
    /// Why the output folder does not accept a write, or null when it does. It writes and removes a probe file, so a scan
    /// asks once for the whole folder rather than once per file.
    /// </summary>
    public static string? OutputFolderProblem(string outputFolder, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        var probe = Path.Combine(outputFolder, $".weir-write-test-{Environment.ProcessId}");
        try
        {
            files.CreateDirectory(outputFolder);
            files.WriteAllBytes(probe, [0]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Weir cannot write to the output folder ({outputFolder}), so it did not start work that would have nowhere to go. The system reported: {exception.Message}.";
        }
        finally
        {
            try
            {
                files.DeleteFile(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A probe left behind is harmless: the next scan overwrites it.
            }
        }

        return null;
    }
}
