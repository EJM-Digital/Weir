using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>A guarded filesystem mutation could not complete safely.</summary>
public sealed class FileLifecycleException : Exception, IPlainFailure
{
    public FileLifecycleException()
    {
    }

    public FileLifecycleException(string message)
        : base(message)
    {
    }

    public FileLifecycleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The message names paths and the system's own words; a person reads this instead.</summary>
    public string? PlainMessage => ToolFailureText.OutputNotPlaced;
}

/// <summary>Safe filesystem/database reconciliation checks and repairs.</summary>
public static class ReconciliationService
{
    private sealed record LibraryFolders(long Id, string Name, string WatchedFolder, string OutputFolder, string WorkFolder);

    /// <summary>The reconciliation report over every library's processing folders.</summary>
    public static async Task<WireObject> BuildReportAsync(UnitOfWork uow) =>
        ReconciliationRules.Report(await ScanProcessingPathsAsync(uow).ConfigureAwait(false));

    /// <summary>Apply one repair action. Throws <see cref="WireValueException"/> with the operator's sentence.</summary>
    public static async Task<WireObject> RepairAsync(UnitOfWork uow, string action, long? dbId, string? path, bool confirm)
    {
        _ = dbId;
        if (action == ReconciliationRules.RemoveTempArtifactAction)
        {
            if (!confirm)
            {
                throw new WireValueException("confirm=true is required before removing a temp artifact.");
            }

            if (path is null || WireStrings.Strip(path).Length == 0)
            {
                throw new WireValueException("path is required for this repair action.");
            }

            var roots = WorkRoots(await ListLibrariesAsync(uow).ConfigureAwait(false));
            if (!ReconciliationRules.IsTempArtifactName(PurePathName(path)))
            {
                throw new WireValueException("Refusing to remove a file that does not look like a temp artifact.");
            }

            var removed = SafeUnlinkUnderRoots(path, roots);
            return new WireObject()
                .Set("applied", removed)
                .Set("message", removed ? "Removed the temp artifact." : "Temp artifact is already gone.");
        }

        throw new WireValueException($"Unknown reconciliation repair action: {action}");
    }

    /// <summary>Deletes a file only when it lies strictly inside one of the roots (never a root itself).</summary>
    public static bool SafeUnlinkUnderRoots(string path, IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        var target = Path.GetFullPath(path);
        foreach (var rawRoot in allowedRoots)
        {
            if (PathContainment.IsUnder(rawRoot, target))
            {
                try
                {
                    if (!File.Exists(target) && !Directory.Exists(target))
                    {
                        return false;
                    }

                    File.Delete(target);
                    return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new FileLifecycleException($"Could not remove {path}: {exception.Message}", exception);
                }
            }
        }

        throw new FileLifecycleException("Refusing to remove a file outside the authorized folder roots.");
    }

    private static async Task<List<ReconciliationIssue>> ScanProcessingPathsAsync(UnitOfWork uow)
    {
        var libraries = await ListLibrariesAsync(uow).ConfigureAwait(false);
        if (libraries.Count == 0)
        {
            return [];
        }

        var issues = new List<ReconciliationIssue>();
        foreach (var library in libraries)
        {
            foreach (var (role, raw) in new[] { ("watched", library.WatchedFolder), ("output", library.OutputFolder), ("work", library.WorkFolder) })
            {
                if (WireStrings.Strip(raw).Length > 0 && !PathExists(raw))
                {
                    issues.Add(new ReconciliationIssue(
                        "configured_folder_missing",
                        "processing",
                        "warning",
                        $"{library.Name} {role} folder is configured but is not currently reachable on disk.",
                        raw,
                        "libraries",
                        library.Id));
                }
            }
        }

        foreach (var root in WorkRoots(libraries))
        {
            // The listing is lazy and skips what it may not read, so asking for it raises nothing.
            var entries = Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true });
            foreach (var entry in entries)
            {
                if (issues.Count >= ReconciliationRules.MaxIssuesPerCategory)
                {
                    return issues;
                }

                if (File.Exists(entry) && ReconciliationRules.IsTempArtifactName(Path.GetFileName(entry)))
                {
                    issues.Add(new ReconciliationIssue(
                        "partial_temp_artifact",
                        "processing",
                        "info",
                        "The work folder contains a temporary artifact from an interrupted operation.",
                        entry,
                        RepairAction: ReconciliationRules.RemoveTempArtifactAction,
                        RequiresConfirmation: true));
                }
            }
        }

        return issues;
    }

    /// <summary>Every library's work folder that exists, resolved.</summary>
    private static List<string> WorkRoots(IEnumerable<LibraryFolders> libraries)
    {
        var roots = new List<string>();
        foreach (var library in libraries)
        {
            if (WireStrings.Strip(library.WorkFolder).Length == 0)
            {
                continue;
            }

            try
            {
                var root = Path.GetFullPath(library.WorkFolder);
                if (Directory.Exists(root))
                {
                    roots.Add(root);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                // A folder that cannot be resolved is skipped.
            }
        }

        return roots;
    }

    private static bool PathExists(string raw) => File.Exists(raw) || Directory.Exists(raw);

    private static string PurePathName(string path) => MediaPathNames.Name(path, OperatingSystem.IsWindows());

    private static Task<List<LibraryFolders>> ListLibrariesAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            "SELECT id, name, watched_folder, output_folder, work_folder FROM libraries ORDER BY display_order, id",
            reader => new LibraryFolders(
                SqliteValues.GetInt64(reader, 0),
                SqliteValues.GetString(reader, 1),
                SqliteValues.GetString(reader, 2),
                SqliteValues.GetString(reader, 3),
                SqliteValues.GetString(reader, 4)));
    }
}
