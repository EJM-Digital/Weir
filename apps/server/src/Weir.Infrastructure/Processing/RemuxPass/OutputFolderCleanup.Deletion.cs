using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.IO;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class OutputFolderCleanup
{
    private async Task DeleteWhenTruthClearsAsync(WireObject output, string prefix, string skipKey, string deletedKey, string cascadeKey, string scope, TitleFolder place, string noun, string logLabel, HandoffOrigin? origin, CancellationToken cancellationToken)
    {
        var answers = await _data.CollectLibraryTruthAsync(scope, cancellationToken).ConfigureAwait(false);
        var acknowledged = await _data.HandoffOutcomeAcknowledgedAsync(origin, cancellationToken).ConfigureAwait(false);
        var truth = LibraryTruthGate.EvaluateForFolder(answers, place.Folder, scope, ResolveOrNull, OperatingSystem.IsWindows(), place.MediaOutputFile, acknowledged);
        output.Set($"{prefix}_truth_check", truth.Check);
        output.Set($"{prefix}_truth_note", truth.Note);
        if (!truth.ClearsDelete)
        {
            output.Set(skipKey, truth.Note);
            if (truth.Check == LibraryTruthVerdict.Skipped)
            {
                _logger.LogWarning("{Label} output cleanup: {Note}", logLabel, truth.Note);
            }

            return;
        }

        if (FolderKeptReason(scope, place) is { } kept)
        {
            output.Set(skipKey, kept);
            output.Set(deletedKey, false);
            _logger.LogInformation("{Label} output cleanup: kept {Folder} — {Reason}", logLabel, place.Folder, kept);
            return;
        }

        try
        {
            Directory.Delete(place.Folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var human = $"Weir could not remove the {noun} because a file or folder was in use or blocked ({exception.Message}).";
            output.Set(skipKey, human);
            output.Set(deletedKey, false);
            output.Set($"{prefix}_truth_check", LibraryTruthVerdict.Skipped);
            output.Set($"{prefix}_truth_note", human);
            _logger.LogWarning("{Label} output cleanup: {Reason}", logLabel, human);
            return;
        }

        output.Set(deletedKey, true);
        output.Set(skipKey, WireNull.Instance);
        var cascade = output.Get(cascadeKey) as WireArray ?? new WireArray();
        CascadeDeleteEmptyParents(Path.GetDirectoryName(place.Folder)!, place.OutputRoot, cascade, _logger, _files);
        output.Set(cascadeKey, cascade);
    }

    /// <summary>Removes empty parents up to, never including, <paramref name="root"/>.</summary>
    public static void CascadeDeleteEmptyParents(string firstParent, string root, WireArray deleted, ILogger logger, IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        ArgumentNullException.ThrowIfNull(logger);
        files ??= PhysicalFileSystem.Instance;
        var resolvedRoot = RemuxPassPaths.Resolve(root);
        var current = RemuxPassPaths.Resolve(firstParent);
        while (!RemuxPassPaths.SamePath(current, resolvedRoot))
        {
            if (!RemuxPassPaths.IsUnder(current, resolvedRoot))
            {
                logger.LogWarning("Cleanup stopped cascade because folder is outside the root ({Folder}).", current);
                break;
            }

            if (!files.DirectoryExists(current))
            {
                break;
            }

            try
            {
                if (files.EnumerateFileSystemInfos(current).Any())
                {
                    break;
                }

                files.DeleteDirectory(current, recursive: false);
                deleted.Items.Add(new WireString(current));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Cleanup could not remove an empty parent folder ({Folder}): {Error}", current, exception.Message);
                break;
            }

            // Strictly under the root, so it is not a filesystem root and has a parent.
            current = Path.GetDirectoryName(current)!;
        }
    }
}
