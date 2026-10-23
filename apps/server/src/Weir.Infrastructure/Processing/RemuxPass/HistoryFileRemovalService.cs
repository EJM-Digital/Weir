using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>What Activity's remove dialog did (#785): whether it went through, and the sentence to show for it.</summary>
public sealed record FileRemovalOutcome(bool Done, string Message);

/// <summary>
/// The "delete" and "keep" choices on Activity's remove dialog for a failed or rejected title whose file is still in
/// the watched folder (#785). "Delete" reuses <see cref="RejectRoutes"/> — the same manager conversation the
/// automatic reject policy has — falling back to deleting the file itself, strictly through
/// <see cref="RemuxPassPaths.CleanupRejectedFile"/>, only when no linked manager can take it. "Keep" records a skip
/// marker (<see cref="FileSkipMarkerStore"/>) and, where the file came from a manager's hand-off, tells that manager
/// it will not be imported. Neither choice ever reports success without telling the file's row and Activity what
/// actually happened; a manager that refuses or does not answer fails the whole action.
/// </summary>
public sealed class HistoryFileRemovalService
{
    private const string DeleteReason = "A person chose to delete this download and ask for another copy.";
    private const string KeepReason = "A person chose to keep this file without processing it again.";

    /// <summary>
    /// A different release can land at the same watched-folder path after a title fails or is rejected. Neither
    /// choice ever acts without first checking the file on disk still is the one Weir actually decided about (#786
    /// review of #785).
    /// </summary>
    private const string ChangedFileMessage =
        "This file has changed since it failed, so Weir won't delete it. It will be looked at again on the next scan.";

    /// <summary>
    /// The refusal for a row with no recorded fingerprint (#786 follow-up) whose confirmed file no longer matches
    /// what is on disk, or was never confirmed at all: unlike <see cref="ChangedFileMessage"/>, Weir has nothing of
    /// its own to compare against, so this says only what is true — the dialog needs reopening.
    /// </summary>
    private const string UnconfirmedChangedMessage =
        "This file changed after you opened this dialog. Close it and try again.";

    private readonly IMediaManagerPorts _ports;
    private readonly MediaManagerConnectionService _connections;
    private readonly HandoffCompletionReporter _reporter;
    private readonly RejectRoutes _routes;
    private readonly LibraryStore _libraries;
    private readonly FileStateStore _files;
    private readonly FileSkipMarkerStore _skipMarkers;
    private readonly IFileSystem _fs;

    public HistoryFileRemovalService(
        IMediaManagerPorts ports,
        MediaManagerConnectionService connections,
        HandoffCompletionReporter reporter,
        RejectRoutes routes,
        LibraryStore libraries,
        FileStateStore files,
        FileSkipMarkerStore skipMarkers,
        IFileSystem? fileSystem = null)
    {
        _fs = fileSystem ?? PhysicalFileSystem.Instance;
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _skipMarkers = skipMarkers ?? throw new ArgumentNullException(nameof(skipMarkers));
    }

    /// <summary>Which manager (if any) "delete" would ask, or that Weir would delete the file itself, and whether "keep" has one to tell.</summary>
    private sealed record ManagerRoute(HandoffReportTarget? HandoffTarget, HandoffOrigin? Origin, IReadOnlyList<ManagerConnection> QueueConnections, string? Label, bool DeleteHandled, bool KeepNotifies)
    {
        public static readonly ManagerRoute None = new(null, null, [], null, false, false);
    }

    /// <summary>What the remove dialog should offer for this title, read before it is shown.</summary>
    public async Task<FileRemovalOptions> EvaluateAsync(UnitOfWork uow, ProcessingFileRecord file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(file);
        if (file.Status is not (ProcessingFileStatuses.ProcessingFailed or ProcessingFileStatuses.Rejected))
        {
            return FileRemovalOptions.PlainRemove;
        }

        var library = await RemuxPassHandler.ResolveLibraryAsync(uow, _libraries, file.LibraryId, null).ConfigureAwait(false);
        var source = library is null ? null : TryResolveSource(library, file.RelativePath, _fs);
        if (source is null || !_fs.FileExists(source))
        {
            return FileRemovalOptions.PlainRemove;
        }

        var route = await ResolveManagerRouteAsync(uow, library!, file.RelativePath, source, cancellationToken).ConfigureAwait(false);
        var fingerprintRecorded = file.FingerprintSizeBytes is not null && file.FingerprintMtimeNs is not null;
        long? unconfirmedSize = null;
        string? unconfirmedModifiedAt = null;
        if (!fingerprintRecorded)
        {
            // What the dialog shows the owner to confirm, since Weir recorded nothing of its own for this row
            // (#786 follow-up). Left null on a read race right after the exists check above; delete/keep refuse all
            // the same with nothing to confirm against.
            try
            {
                var current = SourceFiles.Fingerprint(source, _fs);
                unconfirmedSize = current.SizeBytes;
                unconfirmedModifiedAt = FormatModifiedAt(current.ModifiedTimeNs);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left null.
            }
        }

        var links = await _libraries.ManagerLinksAsync(uow, library!.Id).ConfigureAwait(false);
        return new FileRemovalOptions(true, route.Label, route.DeleteHandled, route.KeepNotifies, fingerprintRecorded, unconfirmedSize, unconfirmedModifiedAt,
            route.DeleteHandled || !links.KeepsOriginals ? null : DeleteRefusal(links));
    }

    /// <summary>
    /// "Delete the download": the manager the file came from, the library's linked manager, or Weir itself.
    /// <paramref name="confirmation"/> is what the dialog showed the owner for a row with no recorded fingerprint
    /// (#786 follow-up); it is ignored when the row has one, and required when it does not.
    /// </summary>
    public async Task<FileRemovalOutcome> DeleteAsync(UnitOfWork uow, ProcessingFileRecord file, FileRemovalConfirmation? confirmation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(file);
        var library = await RemuxPassHandler.ResolveLibraryAsync(uow, _libraries, file.LibraryId, null).ConfigureAwait(false);
        if (library is null)
        {
            return await FinishDeleteAsync(uow, file, new RejectRouteOutcome(false, "The workflow this file belonged to no longer exists.")).ConfigureAwait(false);
        }

        string source;
        try
        {
            source = RemuxPassPaths.ResolveMediaFileUnderRoot(library.WatchedFolder, file.RelativePath, _fs);
        }
        catch (ArgumentException exception)
        {
            return await FinishDeleteAsync(uow, file, new RejectRouteOutcome(false, exception.Message)).ConfigureAwait(false);
        }

        if (!_fs.FileExists(source))
        {
            return await FinishDeleteAsync(uow, file, new RejectRouteOutcome(false, "The file is no longer in the watched folder, so there is nothing to delete.")).ConfigureAwait(false);
        }

        if (!TryVerifyFingerprint(file, source, confirmation, _fs, out _, out var refusal))
        {
            return await FinishDeleteAsync(uow, file, new RejectRouteOutcome(false, refusal)).ConfigureAwait(false);
        }

        var watchedRoot = RemuxPassPaths.Resolve(library.WatchedFolder);
        var links = await _libraries.ManagerLinksAsync(uow, library.Id).ConfigureAwait(false);
        var route = await ResolveManagerRouteAsync(uow, library, file.RelativePath, source, cancellationToken).ConfigureAwait(false);

        // Released before any network call or disk delete, the way the automatic reject job releases it (#708):
        // no other lane ever waits on this one for SQLite's write lock.
        await uow.CommitAsync().ConfigureAwait(false);

        // A manager that cannot take the rejection (no capability, or none linked at all) never gets asked. Weir deletes the file
        // itself only when no manager is linked, since a linked manager's download belongs to it and its download client.
        var outcome = !route.DeleteHandled
            ? SelfDelete(watchedRoot, source, links, _fs)
            : route.HandoffTarget is not null
                ? await _routes.ThroughHandoffAsync(route.HandoffTarget, route.Origin!, source, watchedRoot, DeleteReason, null, links, cancellationToken).ConfigureAwait(false)
                : await _routes.ThroughQueueAsync(route.QueueConnections, source, cancellationToken).ConfigureAwait(false);

        if (outcome.Done)
        {
            // A stale "keep" marker from before this file last changed would otherwise linger, pointless, in the
            // Kept files list once its own file is deleted (#786 review of #785).
            await _skipMarkers.ClearAsync(uow, file.LibraryId, file.RelativePath).ConfigureAwait(false);
        }

        return await FinishDeleteAsync(uow, file, outcome).ConfigureAwait(false);
    }

    /// <summary>Clears a "keep" marker for this file, when there is one — used by "delete" above and by "retry" on the remove dialog.</summary>
    public Task ClearSkipMarkerAsync(UnitOfWork uow, long libraryId, string relativePath) => _skipMarkers.ClearAsync(uow, libraryId, relativePath);

    /// <summary>
    /// "Keep the file, but don't process it again": a skip marker, and a hand-off's manager told it will not be
    /// imported. <paramref name="confirmation"/> is what the dialog showed the owner for a row with no recorded
    /// fingerprint (#786 follow-up); it is ignored when the row has one, and required when it does not.
    /// </summary>
    public async Task<FileRemovalOutcome> KeepAsync(UnitOfWork uow, ProcessingFileRecord file, FileRemovalConfirmation? confirmation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(file);
        var library = await RemuxPassHandler.ResolveLibraryAsync(uow, _libraries, file.LibraryId, null).ConfigureAwait(false);
        if (library is null)
        {
            return await FinishKeepAsync(uow, file, new RejectRouteOutcome(false, "The workflow this file belonged to no longer exists.")).ConfigureAwait(false);
        }

        string source;
        try
        {
            source = RemuxPassPaths.ResolveMediaFileUnderRoot(library.WatchedFolder, file.RelativePath, _fs);
        }
        catch (ArgumentException exception)
        {
            return await FinishKeepAsync(uow, file, new RejectRouteOutcome(false, exception.Message)).ConfigureAwait(false);
        }

        if (!_fs.FileExists(source))
        {
            return await FinishKeepAsync(uow, file, new RejectRouteOutcome(false, "The file is no longer in the watched folder, so there is nothing to keep.")).ConfigureAwait(false);
        }

        if (!TryVerifyFingerprint(file, source, confirmation, _fs, out var fingerprint, out var refusal))
        {
            return await FinishKeepAsync(uow, file, new RejectRouteOutcome(false, refusal)).ConfigureAwait(false);
        }

        var route = await ResolveManagerRouteAsync(uow, library, file.RelativePath, source, cancellationToken).ConfigureAwait(false);

        // Released before any network call, the same as DeleteAsync above.
        await uow.CommitAsync().ConfigureAwait(false);

        var outcome = route.HandoffTarget is not null
            ? await KeepThroughHandoffAsync(route.HandoffTarget, route.Origin!, cancellationToken).ConfigureAwait(false)
            : new RejectRouteOutcome(true, "Weir will leave this file alone until it changes.");
        if (!outcome.Done)
        {
            return await FinishKeepAsync(uow, file, outcome).ConfigureAwait(false);
        }

        await _skipMarkers.SetAsync(uow, library.Id, file.RelativePath, fingerprint.SizeBytes, fingerprint.ModifiedTimeNs).ConfigureAwait(false);
        return await FinishKeepAsync(uow, file, outcome).ConfigureAwait(false);
    }

    /// <summary>The same failed-report shape a reject sends, worded for a file Weir is keeping rather than rejecting.</summary>
    private async Task<RejectRouteOutcome> KeepThroughHandoffAsync(HandoffReportTarget target, HandoffOrigin origin, CancellationToken cancellationToken)
    {
        var label = target.Connection.Label;
        var result = new WireObject().Set("ok", false).Set("outcome", "failed").Set("reason", KeepReason);
        var body = CompletionReports.BuildCompletionBody(origin, result, rejected: false);
        var delivery = await _reporter.PostHandoffReportAsync(target, body, cancellationToken).ConfigureAwait(false);
        var report = new RejectDeliveryReport(target, body, delivery);
        if (!delivery.Accepted)
        {
            var status = delivery.Status.StartsWith("failed: ", StringComparison.Ordinal) ? delivery.Status["failed: ".Length..] : delivery.Status;
            return new RejectRouteOutcome(false, $"{label} did not accept that this file will not be imported ({status}), so Weir did not keep it.", label, null, report);
        }

        return new RejectRouteOutcome(true, $"Weir will leave this file alone until it changes. {label} was told it will not be imported.", label, null, report);
    }

    /// <summary>Why Weir will not delete a linked workflow's file itself, in the words the dialog and the refusal share.</summary>
    private static string DeleteRefusal(WorkflowManagerLinks links) =>
        $"{links.KeptOriginalReason} Weir did not delete the file; remove the download from your download client.";

    private static RejectRouteOutcome SelfDelete(string watchedRoot, string source, WorkflowManagerLinks links, IFileSystem files)
    {
        if (links.KeepsOriginals)
        {
            return new RejectRouteOutcome(false, DeleteRefusal(links));
        }

        var cleanup = RemuxPassPaths.CleanupRejectedFile(watchedRoot, source, "delete_file", files);
        return cleanup.Deleted
            ? new RejectRouteOutcome(true, "Weir deleted the file itself: no linked media manager could remove it.")
            : new RejectRouteOutcome(false, cleanup.Detail);
    }

    private async Task<ManagerRoute> ResolveManagerRouteAsync(UnitOfWork uow, ProcessingLibraryRecord library, string relativePath, string source, CancellationToken cancellationToken)
    {
        var originJson = await HandoffOriginCarry.FindAsync(uow, library.Id, relativePath).ConfigureAwait(false);
        var origin = originJson is not null ? HandoffOrigin.FromPayload(new WireObject().Set("origin", originJson)) : null;
        if (origin is not null && _ports.PortForKind(origin.SourceKey) is { } originPort && !originPort.Capabilities().RemovesQueueItems)
        {
            var (target, _) = await _reporter.ResolveHandoffTargetAsync(uow, origin).ConfigureAwait(false);
            if (target is not null)
            {
                var canReject = await CanRejectThroughHandoffAsync(target, cancellationToken).ConfigureAwait(false);
                return new ManagerRoute(target, origin, [], target.Connection.Label, canReject, true);
            }
        }

        var connectionIds = await _libraries.ManagerConnectionIdsAsync(uow, library.Id).ConfigureAwait(false);
        var linked = await _connections.ConnectionsByIdAsync(uow, connectionIds).ConfigureAwait(false);
        var queueConnections = linked.Where(connection => _ports.PortForKind(connection.Kind)?.Capabilities().RemovesQueueItems == true).ToList();
        if (queueConnections.Count == 0)
        {
            return ManagerRoute.None;
        }

        // The exact rule ThroughQueueAsync itself uses to pick a download (#786 review of #785): naming a manager
        // the action would not actually use, or the reverse, would tell the operator one thing and do another.
        var match = await _routes.MatchQueueItemAsync(queueConnections, source, cancellationToken).ConfigureAwait(false);
        return match.Found
            ? new ManagerRoute(null, null, queueConnections, match.Connection!.Label, true, false)
            : ManagerRoute.None;
    }

    private async Task<bool> CanRejectThroughHandoffAsync(HandoffReportTarget target, CancellationToken cancellationToken)
    {
        var port = _ports.PortForKind(target.Connection.Kind);
        if (port is null)
        {
            return false;
        }

        var description = await port.DescribeAsync(target.Connection, cancellationToken).ConfigureAwait(false);
        return description.Status == SignalStatus.Reported && description.AdvertisedCapabilities?.Contains(RejectSupportRules.RejectCapability) == true;
    }

    /// <summary>
    /// Whether the file on disk right now is still the one Weir decided about when this title became failed or
    /// rejected (#786 review of #785, and its follow-up). A row with a recorded fingerprint is checked against that;
    /// a row without one (from before migration 0025, or one that could not be read at the time) is checked against
    /// <paramref name="confirmation"/> — what the remove dialog showed the owner — and refused outright when there
    /// is none to check, rather than guessing. <paramref name="refusalMessage"/> names which of the two is true, so
    /// the caller never blames "since it failed" on a file Weir never actually fingerprinted.
    /// </summary>
    private static bool TryVerifyFingerprint(ProcessingFileRecord file, string source, FileRemovalConfirmation? confirmation, IFileSystem files, out SourceFingerprint fingerprint, out string refusalMessage)
    {
        fingerprint = default;
        if (file.FingerprintSizeBytes is { } size && file.FingerprintMtimeNs is { } mtime)
        {
            refusalMessage = ChangedFileMessage;
            try
            {
                fingerprint = SourceFiles.Fingerprint(source, files);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            return fingerprint.SizeBytes == size && fingerprint.ModifiedTimeNs == mtime;
        }

        refusalMessage = UnconfirmedChangedMessage;
        if (confirmation is not { } confirmed)
        {
            return false;
        }

        try
        {
            fingerprint = SourceFiles.Fingerprint(source, files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return fingerprint.SizeBytes == confirmed.SizeBytes && FormatModifiedAt(fingerprint.ModifiedTimeNs) == confirmed.ModifiedAt;
    }

    /// <summary>
    /// A file's modified time, rounded to the whole second a person is shown, as stable UTC text a browser can
    /// round-trip exactly. The raw nanoseconds <see cref="SourceFingerprint"/> carries would lose precision the
    /// moment a browser parsed it into a JSON number — this never needs to survive that.
    /// </summary>
    private static string FormatModifiedAt(long modifiedTimeNs) =>
        Timestamp.FromUtc(DateTime.UnixEpoch.AddSeconds(modifiedTimeNs / 1_000_000_000)).ToWireText();

    private static string? TryResolveSource(ProcessingLibraryRecord library, string relativePath, IFileSystem files)
    {
        try
        {
            return RemuxPassPaths.ResolveMediaFileUnderRoot(library.WatchedFolder, relativePath, files);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private Task<FileRemovalOutcome> FinishDeleteAsync(UnitOfWork uow, ProcessingFileRecord file, RejectRouteOutcome outcome) =>
        FinishAsync(
            uow, file, outcome, ActivityEventTypes.ProcessingFileRemovalDeleted,
            successTitle: fileName => $"{fileName} was removed from Activity and its download deleted",
            failureTitle: fileName => $"{fileName} could not be deleted");

    private Task<FileRemovalOutcome> FinishKeepAsync(UnitOfWork uow, ProcessingFileRecord file, RejectRouteOutcome outcome) =>
        FinishAsync(
            uow, file, outcome, ActivityEventTypes.ProcessingFileRemovalKept,
            successTitle: fileName => $"{fileName} was kept without processing it again",
            failureTitle: fileName => $"{fileName} could not be kept",
            // #786 review of #785: this is a deliberate hold, not the generic "Weir could not process" wording
            // RecordHandoffReportAsync would otherwise give a hold it did not choose.
            handoffReportTitle: (manager, fileName) => $"Told {manager} you chose to keep {fileName} without processing it");

    /// <summary>Records what happened in Activity, forgets the file's row on success, and never leaves a failure unrecorded.</summary>
    private async Task<FileRemovalOutcome> FinishAsync(
        UnitOfWork uow, ProcessingFileRecord file, RejectRouteOutcome outcome, string eventType, Func<string, string> successTitle, Func<string, string> failureTitle,
        Func<string, string, string>? handoffReportTitle = null)
    {
        if (outcome.Report is { } report)
        {
            await HandoffCompletionReporter.RecordHandoffReportAsync(uow, report.Target, report.Body, report.Delivery, file.RelativePath, handoffReportTitle).ConfigureAwait(false);
        }

        var detail = new WireObject()
            .Set("relative_media_path", file.RelativePath)
            .Set("library_id", file.LibraryId)
            .Set("manager", outcome.Manager)
            .Set("message", outcome.Reason)
            .Set("trigger", "history_remove_dialog")
            .Set("result", outcome.Done ? "success" : "failed");
        foreach (var (key, value) in outcome.DetailOrEmpty.Items)
        {
            detail.Set(key, value);
        }

        if (outcome.Done)
        {
            await _files.ForgetAsync(uow, file.Id).ConfigureAwait(false);
        }

        var fileName = MediaPathNames.Name(file.RelativePath, OperatingSystem.IsWindows());
        var title = outcome.Done ? successTitle(fileName) : failureTitle(fileName);
        await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
            eventType, "processing", title, WireStrings.Slice(WireJsonWriter.Dumps(detail, WireJsonFormat.Compact), 10_000))).ConfigureAwait(false);
        await uow.CommitAsync().ConfigureAwait(false);
        return new FileRemovalOutcome(outcome.Done, outcome.Reason);
    }
}
