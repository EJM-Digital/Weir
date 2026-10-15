using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>Reports Weir owes a manager that was not answering when a hand-off finished (item 5 of #652).</summary>
public sealed partial class HandoffCompletionReporter
{
    /// <summary>
    /// Send every report Weir still owes a manager of this kind because it was not answering, or because nothing
    /// attempted delivery before this owed report was persisted (a claim recorded just before a crash, say, #667).
    /// The heartbeat calls this once the manager answers its connection test. A report the manager answers, accepted
    /// or refused, stops being owed, and the Activity of each file it covers stops saying Weir is waiting; one it still does
    /// not answer stays owed. Each report is committed on its own. Returns how many the manager answered.
    /// </summary>
    public async Task<int> SendWaitingReportsAsync(UnitOfWork uow, string sourceKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var answered = 0;
        foreach (var (handoffId, reportJson) in await HandoffLedgerStore.PendingReportsAsync(uow, sourceKey).ConfigureAwait(false))
        {
            if (PendingReport.Parse(reportJson) is not { } pending)
            {
                await HandoffLedgerStore.SetPendingReportAsync(uow, sourceKey, handoffId, null).ConfigureAwait(false);
                await uow.CommitAsync().ConfigureAwait(false);
                continue;
            }

            var origin = new HandoffOrigin(sourceKey, handoffId, pending.CallbackPath, pending.ReleaseName, pending.ManagerLibraryId);
            var (target, _) = await ResolveHandoffTargetAsync(uow, origin).ConfigureAwait(false);
            if (target is null)
            {
                continue;
            }

            var delivery = await DeliverOwedReportAsync(uow, sourceKey, handoffId, target, pending, cancellationToken).ConfigureAwait(false);
            if (IsNotAnswering(delivery))
            {
                continue;
            }

            LogWaitingReportSent(_logger, target.Connection.Label, handoffId, delivery.Status);
            answered++;
        }

        return answered;
    }

    /// <summary>
    /// Stops owing the reports <paramref name="owed"/>, because the person told Weir to give up on them: each hand-off stops holding its
    /// report, and the Activity of each file it covers says Weir will not tell <paramref name="manager"/> about it, in place of the line
    /// saying it is waiting. Nothing is sent to the manager. Does not commit.
    /// </summary>
    public async Task GiveUpOwedReportsAsync(UnitOfWork uow, string manager, IReadOnlyList<OwedReportRow> owed)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(owed);
        foreach (var report in owed)
        {
            if (PendingReport.Parse(report.ReportJson) is { LibraryId: { } library } pending)
            {
                foreach (var relativePath in pending.Files)
                {
                    await ReplaceFileSentenceAsync(
                        uow, library, relativePath, ManagerWaitMessages.ReportWaiting(manager), ManagerWaitMessages.ReportGivenUp(manager)).ConfigureAwait(false);
                }
            }

            await HandoffLedgerStore.SetPendingReportAsync(uow, report.SourceKey, report.HandoffId, null).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempt one delivery of an owed report, and settle it: still not answering, it stays owed and the Activity of
    /// each file it covers says so (added once, whether this is the first attempt or a retry); answered, accepted or
    /// refused, it stops being owed and Activity records it. Commits.
    /// </summary>
    private async Task<HandoffReportDelivery> DeliverOwedReportAsync(
        UnitOfWork uow, string sourceKey, string handoffId, HandoffReportTarget target, PendingReport pending, CancellationToken cancellationToken)
    {
        var delivery = await PostHandoffReportAsync(target, pending.Body, cancellationToken).ConfigureAwait(false);
        if (IsNotAnswering(delivery))
        {
            if (pending.LibraryId is { } waitingLibrary)
            {
                foreach (var relativePath in pending.Files)
                {
                    await AppendFileSentenceAsync(uow, waitingLibrary, relativePath, ManagerWaitMessages.ReportWaiting(target.Connection.Label)).ConfigureAwait(false);
                }
            }

            await uow.CommitAsync().ConfigureAwait(false);
            return delivery;
        }

        await HandoffLedgerStore.SetPendingReportAsync(uow, sourceKey, handoffId, null).ConfigureAwait(false);
        await RecordHandoffReportAsync(uow, target, pending.Body, delivery, pending.Subject).ConfigureAwait(false);
        if (pending.LibraryId is { } library)
        {
            foreach (var relativePath in pending.Files)
            {
                await ReplaceFileSentenceAsync(
                    uow,
                    library,
                    relativePath,
                    ManagerWaitMessages.ReportWaiting(target.Connection.Label),
                    delivery.Accepted ? ManagerWaitMessages.ReportDelivered(target.Connection.Label) : string.Empty).ConfigureAwait(false);
            }
        }

        await uow.CommitAsync().ConfigureAwait(false);
        return delivery;
    }

    /// <summary>A file's status reason with one sentence swapped for another (or dropped), so Activity reads as things are now.</summary>
    private static Task<int> ReplaceFileSentenceAsync(UnitOfWork uow, long libraryId, string relativePath, string sentence, string replacement) =>
        uow.ExecuteAsync(
            "UPDATE files SET status_reason = trim(replace(status_reason, $old, $new)), updated_at = CURRENT_TIMESTAMP " +
            "WHERE library_id = $library AND relative_path = $path AND instr(status_reason, $old) > 0",
            ("$old", sentence),
            ("$new", replacement),
            ("$library", libraryId),
            ("$path", relativePath));

    /// <summary>A file's status reason with a sentence added once.</summary>
    private static Task<int> AppendFileSentenceAsync(UnitOfWork uow, long libraryId, string relativePath, string sentence) =>
        uow.ExecuteAsync(
            "UPDATE files SET status_reason = trim(status_reason || ' ' || $sentence), updated_at = CURRENT_TIMESTAMP " +
            "WHERE library_id = $library AND relative_path = $path AND instr(status_reason, $sentence) = 0",
            ("$sentence", sentence),
            ("$library", libraryId),
            ("$path", relativePath));

    /// <summary>
    /// A report Weir owes a manager that was not answering, as saved on the hand-off row. <see cref="Subject"/> is the path
    /// Activity names the report by (the file, or the folder of a hand-off of several files); <see cref="Files"/> are the
    /// files whose Activity says Weir is waiting.
    /// </summary>
    private sealed record PendingReport(
        string? CallbackPath, string? ReleaseName, string? ManagerLibraryId, WireObject Body, long? LibraryId, string? Subject, IReadOnlyList<string> Files)
    {
        public string ToJson() => WireJsonWriter.Dumps(
            new WireObject()
                .Set("callback_path", CallbackPath)
                .Set("release_name", ReleaseName)
                .Set("manager_library_id", ManagerLibraryId)
                .Set("body", Body)
                .Set("library_id", LibraryId)
                .Set("relative_media_path", Subject)
                .Set("relative_media_paths", HandoffOutputFiles.ToJson(Files)),
            WireJsonFormat.Compact);

        /// <summary>The saved report, or null when it cannot be read. One saved before it listed its files names only its own file.</summary>
        public static PendingReport? Parse(string json)
        {
            try
            {
                if (WireJsonParser.Parse(json) is not WireObject dict || dict.Get("body") is not WireObject body)
                {
                    return null;
                }

                var subject = HandoffOrigin.OptionalText(dict.Get("relative_media_path"));
                IReadOnlyList<string> files = dict.Get("relative_media_paths") is WireArray listed
                    ? [.. listed.Items.OfType<WireString>().Select(item => item.Value)]
                    : subject is null ? [] : [subject];
                return new PendingReport(
                    HandoffOrigin.OptionalText(dict.Get("callback_path")),
                    HandoffOrigin.OptionalText(dict.Get("release_name")),
                    HandoffOrigin.OptionalText(dict.Get("manager_library_id")),
                    body,
                    dict.Get("library_id") is WireInteger library ? (long)library.Value : null,
                    subject,
                    files);
            }
            catch (WireJsonDecodeException)
            {
                return null;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Manager} is answering again; the hand-off report Weir owed it for {HandoffId}: {Status}")]
    private static partial void LogWaitingReportSent(ILogger logger, string manager, string handoffId, string status);
}
