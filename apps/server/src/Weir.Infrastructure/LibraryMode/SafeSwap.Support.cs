using Microsoft.Extensions.Logging;
using Weir.Core.LibraryMode;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// <see cref="SafeSwap"/>'s private support routines: rollback, the destination a kept original resolves to, and the
/// small filesystem and fingerprint checks its two public operations share.
/// </summary>
public sealed partial class SafeSwap
{
    /// <summary>
    /// #735: where the original will go once the swap commits, or null while the setting is off. Reserves the name
    /// atomically (<see cref="OriginalsMover.Reserve"/>) rather than just checking it is free: a plain check here and a
    /// concurrent clean's plain check a moment later could both see the same name free and both write to it.
    /// </summary>
    private string? ResolveKeepDestination(KeepOriginalOptions? keepOriginal, string originalPath)
    {
        if (keepOriginal is null)
        {
            return null;
        }

        var containingFolder = OriginalsPathPlanner.ContainingFolder(keepOriginal.LibraryFolders, originalPath) ?? DirectoryOf(originalPath);
        var candidate = OriginalsPathPlanner.DestinationPath(containingFolder, keepOriginal.OriginalsFolder, originalPath);
        return OriginalsMover.Reserve(_files, candidate);
    }

    /// <summary>Whether two fingerprints describe the same unchanged file. Device and inode are compared only when both were read.</summary>
    internal static bool SameFile(SourceFingerprint current, SourceFingerprint recorded)
    {
        if (current.SizeBytes != recorded.SizeBytes || current.ModifiedTimeNs != recorded.ModifiedTimeNs)
        {
            return false;
        }

        var identityKnown = (current.Device, current.Inode) != (0, 0) && (recorded.Device, recorded.Inode) != (0, 0);
        return !identityKnown || (current.Device == recorded.Device && current.Inode == recorded.Inode);
    }

    internal static string DirectoryOf(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? "." : directory;
    }

    /// <summary>Undo a swap that did not commit, judging from the files alone. Failures are logged; the sweep finishes the job.</summary>
    private async Task RollbackAsync(long jobId, string originalPath)
    {
        var report = SwapRecoverySweep.RecoverFile(_files, originalPath, _logger, restoreQuietly: true);
        if (report.Problems > 0)
        {
            _logger.LogWarning("Library swap rollback left files for the startup sweep job_id={JobId} path={Path}", jobId, originalPath);
            return;
        }

        try
        {
            await _journal.RecordAsync(new SwapJournalEntry(jobId, originalPath, SwapJournalState.RolledBack), CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A rolled-back swap already left the original in place; a failed record only loses the note.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Library swap rolled back but not recorded job_id={JobId} path={Path}", jobId, originalPath);
        }
    }

    private bool? Exists(string path)
    {
        try
        {
            return _files.FileExists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
