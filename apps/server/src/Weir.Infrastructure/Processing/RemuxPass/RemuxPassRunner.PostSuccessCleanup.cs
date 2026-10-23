using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassRunner
{
    /// <summary>Copies the source's sidecars to the output before any source cleanup can delete them.</summary>
    private async Task MigrateSidecarsBeforeCleanupAsync(string src, string? finalOutputFile, IReadOnlyList<string> patterns, bool preserveTimestamps, WireObject output)
    {
        OutputFolderCleanup.SetDefault(output, "sidecars_migrated", new WireArray());
        OutputFolderCleanup.SetDefault(output, "sidecars_skipped", new WireArray());
        OutputFolderCleanup.SetDefault(output, "sidecar_migration_blocked", WireBool.False);
        OutputFolderCleanup.SetDefault(output, "sidecar_migration_blocked_reason", WireNull.Instance);
        OutputFolderCleanup.SetDefault(output, "original_timestamps_note", WireNull.Instance);
        if (finalOutputFile is null || patterns.Count == 0)
        {
            return;
        }

        var result = await SidecarMigration.MigrateAsync(src, finalOutputFile, patterns, preserveTimestamps).ConfigureAwait(false);
        output.Set("sidecars_migrated", StringList(result.Migrated.Select(m => Path.GetFileName(m.Destination))));
        output.Set("sidecars_skipped", StringList(result.Skipped));
        if (result.BlocksSourceDeletion)
        {
            output.Set("sidecar_migration_blocked", true);
            output.Set("sidecar_migration_blocked_reason", result.BlockingReason);
            _logger.LogWarning("Sidecar migration: {Reason}", result.BlockingReason);
        }

        if (preserveTimestamps && SidecarMigration.ApplyOriginalTimestamps(src, finalOutputFile, Files) is { } problem)
        {
            // Never fatal: the output is correct either way.
            output.Set("original_timestamps_note", problem);
            _logger.LogInformation("Timestamps: {Problem}", problem);
        }
    }

    /// <summary>Why the source is still in the watched folder after a successful pass, when the library asks for that.</summary>
    public const string KeptOriginalReason =
        "This workflow keeps the original download after cleaning, so Weir left it in the watched folder for your download client.";

    private static void InitFolderCleanupFields(WireObject output)
    {
        OutputFolderCleanup.SetDefault(output, "source_folder_deleted", WireBool.False);
        OutputFolderCleanup.SetDefault(output, "source_folder_path", WireNull.Instance);
        OutputFolderCleanup.SetDefault(output, "source_folder_skip_reason", WireNull.Instance);
        OutputFolderCleanup.SetDefault(output, "output_completeness_check", WireNull.Instance);
        OutputFolderCleanup.SetDefault(output, "output_size_bytes", WireNull.Instance);
        OutputFolderCleanup.SetDefault(output, "source_size_bytes", WireNull.Instance);
        OutputFolderCleanup.SetDefault(output, "cascade_folders_deleted", new WireArray());
        OutputFolderCleanup.SetDefault(output, "output_completeness_note", WireNull.Instance);
    }

    /// <summary>
    /// Source cleanup after a successful pass: Movies may remove the whole release folder and its empty parents; TV hands
    /// over to the season-folder cleanup.
    /// </summary>
    private async Task HandleCleanupAfterSuccessAsync(PassContext context, WireObject output, string? finalOutputFile, CancellationToken cancellationToken)
    {
        // The original download stays where it is — a torrent still seeding, or a download a media manager owns: nothing in the
        // watched folder is touched, not the file, its release or season folder, nor its sidecars, which were copied, never
        // moved. A workflow linked to a media manager keeps it whatever its saved setting says. This is the one gate for
        // every post-success source removal: Movies' release folder below and TV's season-folder cleanup.
        var runtime = context.Request.Runtime;
        if (!runtime.RemovesOriginals)
        {
            InitFolderCleanupFields(output);
            SkippedTvSeasonFolderCleanup.InitFields(output);
            output.Set("source_deleted_after_success", false);
            output.Set(runtime.ManagerLinks.KeepsOriginals ? "source_kept_by_manager_link" : "source_kept_by_library_setting", true);
            output.Set(context.Scope == "tv" ? "tv_season_folder_skip_reason" : "source_folder_skip_reason", runtime.KeptOriginalReason);
            return;
        }

        if (output.Get("sidecar_migration_blocked") is { IsTruthy: true })
        {
            InitFolderCleanupFields(output);
            output.Set("source_deleted_after_success", false);
            output.Set("source_folder_skip_reason", output.Get("sidecar_migration_blocked_reason") is { IsTruthy: true } blocked
                ? blocked
                : new WireString("Weir did not remove the source folder because a file set to travel with the video could not be copied."));
            _logger.LogWarning("Cleanup blocked by sidecar migration: {Reason}", WireConvert.Str(output["source_folder_skip_reason"]));
            return;
        }

        if (context.Scope == "tv")
        {
            await _tvSeasonCleanup.RunAsync(
                new TvSeasonCleanupContext(output, context.Request.Runtime, context.Source, context.WatchedRoot, context.MinAge, context.Request.CurrentJobId, output.Copy(), finalOutputFile),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        InitFolderCleanupFields(output);
        var src = context.Source;
        var watched = context.WatchedRoot;
        var movieFolder = Path.GetDirectoryName(src)!;
        if (RemuxPassPaths.SamePath(movieFolder, watched))
        {
            output.Set("source_folder_skip_reason", "The video file sits directly in the watched folder root, so Weir does not remove a release folder here.");
            output.Set("source_deleted_after_success", false);
            _logger.LogDebug("Movies cleanup: {Path} sits directly in the watched folder {Root}, so there is no release folder to remove.", src, watched);
            return;
        }

        output.Set("source_folder_path", movieFolder);
        var outputFile = finalOutputFile ?? Path.Join(context.OutputDirectory, RemuxPassPaths.RelativeTo(src, watched)!);
        var check = CheckOutputFileCompleteness(outputFile, src, files: Files);
        foreach (var (key, value) in check.Items)
        {
            if (key != "output_completeness_note" || value.IsTruthy)
            {
                output.Set(key, value);
            }
        }

        if (check.Get("output_completeness_check") is not WireString { Value: "passed" })
        {
            output.Set("source_folder_skip_reason", check["output_completeness_note"]);
            output.Set("source_deleted_after_success", false);
            _logger.LogWarning("Movies cleanup: skipped — {Reason}", WireConvert.Str(output["source_folder_skip_reason"]));
            return;
        }

        ReleaseRemoval removal;
        try
        {
            removal = ReleaseFolderRemoval.Remove(watched, src, context.Request.Runtime.MediaExtensionsCsv);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message = $"Weir could not remove the release folder ({movieFolder}): {exception.Message}";
            _logger.LogWarning("Movies cleanup: {Message}", message);
            output.Set("source_folder_skip_reason", message);
            output.Set("source_deleted_after_success", false);
            output.Set("source_folder_deleted", false);
            return;
        }

        if (!removal.FolderRemoved)
        {
            // A pack or category folder keeps its other videos; a linked folder is left alone entirely.
            _logger.LogInformation("Movies cleanup: kept the release folder {Folder} — {Reason}", movieFolder, removal.Reason);
            output.Set("source_folder_skip_reason", removal.Reason);
            output.Set("source_deleted_after_success", removal.FileRemoved);
            output.Set("source_folder_deleted", false);
            return;
        }

        output.Set("source_folder_deleted", true);
        output.Set("source_deleted_after_success", true);
        output.Set("source_folder_skip_reason", WireNull.Instance);
        var cascade = output.Get("cascade_folders_deleted") as WireArray ?? new WireArray();
        OutputFolderCleanup.CascadeDeleteEmptyParents(Path.GetDirectoryName(movieFolder)!, watched, cascade, _logger, Files);
        output.Set("cascade_folders_deleted", cascade);
    }

    /// <summary>Checks the output exists, is not empty and is not under 1% of the source.</summary>
    public static WireObject CheckOutputFileCompleteness(string outputFile, string sourceFile, bool tv = false, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        if (!files.FileExists(outputFile))
        {
            return Completeness("failed", null, null, "The output file is missing at the expected path.");
        }

        long outSize;
        long srcSize;
        try
        {
            outSize = files.GetLength(outputFile);
            srcSize = files.GetLength(sourceFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Completeness("failed", null, null, $"Weir could not read the file size ({exception.Message}).");
        }

        if (outSize <= 0)
        {
            return Completeness("failed", outSize, srcSize, "The output file is empty (zero bytes).");
        }

        if (srcSize > 0 && outSize < Math.Max(1, srcSize / 100))
        {
            return Completeness(
                "failed",
                outSize,
                srcSize,
                tv
                    ? "The output file is much smaller than the source (under 1% of source size), so Weir blocked TV season cleanup as a safety step."
                    : "The output file is much smaller than the source (under 1% of source size), so Weir skipped removing the release folder as a safety step.");
        }

        return Completeness("passed", outSize, srcSize, null);

        static WireObject Completeness(string check, long? output, long? source, string? note) => new WireObject()
            .Set("output_completeness_check", check)
            .Set("output_size_bytes", output)
            .Set("source_size_bytes", source)
            .Set("output_completeness_note", note);
    }

    private Task RunScopeOutputCleanupAsync(PassContext context, WireObject output, string? finalOutputFile, CancellationToken cancellationToken) =>
        context.Scope == "tv"
            ? _outputCleanup.RunTvAsync(output, context.Request.Runtime, context.WatchedRoot, context.Source, finalOutputFile, context.Request.CurrentJobId, context.Scope, context.Request.Origin, cancellationToken)
            : _outputCleanup.RunMovieAsync(output, context.Request.Runtime, context.WatchedRoot, context.Source, finalOutputFile, context.RelativeMediaPath, context.Request.CurrentJobId, context.Scope, context.Request.Origin, cancellationToken);
}
