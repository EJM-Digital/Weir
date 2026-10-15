using System.Globalization;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Time;
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
    /// not answer stays owed, and so does one it answered that it could not take yet (<see cref="IsTransientFailure"/>), which is
    /// sent again once the wait <see cref="ReportRetrySchedule"/> sets has passed. Each report is committed on its own. Returns how many the
    /// manager answered for good.
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

            if (pending.NotBefore > _time.GetUtcNow())
            {
                continue;
            }

            var (delivery, stillOwed) = await DeliverOwedReportAsync(uow, sourceKey, handoffId, target, pending, cancellationToken).ConfigureAwait(false);
            if (stillOwed)
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
    /// Attempt one delivery of an owed report, and settle it: not answered, or answered that it cannot be taken yet, it stays owed
    /// (the wait before the next attempt is saved with it) and the Activity of each file it covers says so (added once, whether this is
    /// the first attempt or a retry); answered otherwise, accepted or refused, or refused for a week, it stops being owed and Activity
    /// records it. Commits. <c>StillOwed</c> says which.
    /// </summary>
    private async Task<(HandoffReportDelivery Delivery, bool StillOwed)> DeliverOwedReportAsync(
        UnitOfWork uow, string sourceKey, string handoffId, HandoffReportTarget target, PendingReport pending, CancellationToken cancellationToken)
    {
        var delivery = await PostHandoffReportAsync(target, pending.Body, cancellationToken).ConfigureAwait(false);
        if (ToSendAgain(pending, delivery, _time.GetUtcNow()) is { } retry)
        {
            if (!ReferenceEquals(retry, pending))
            {
                await HandoffLedgerStore.SetPendingReportAsync(uow, sourceKey, handoffId, retry.ToJson()).ConfigureAwait(false);
            }

            if (pending.LibraryId is { } waitingLibrary)
            {
                foreach (var relativePath in pending.Files)
                {
                    await AppendFileSentenceAsync(uow, waitingLibrary, relativePath, ManagerWaitMessages.ReportWaiting(target.Connection.Label)).ConfigureAwait(false);
                }
            }

            await uow.CommitAsync().ConfigureAwait(false);
            return (delivery, true);
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
        return (delivery, false);
    }

    /// <summary>
    /// The report to keep owed after <paramref name="delivery"/>, or null when the delivery settles it. A manager that did not answer
    /// leaves the report as it was, since the heartbeat only sends once the manager answers its connection test; one that answered
    /// that it cannot take it yet gets a wait before the next attempt (<see cref="ReportRetrySchedule"/>) until it has refused for too long.
    /// </summary>
    private static PendingReport? ToSendAgain(PendingReport pending, HandoffReportDelivery delivery, DateTimeOffset now)
    {
        if (!IsTransientFailure(delivery))
        {
            return null;
        }

        var kept = IsNotAnswering(delivery) ? pending : pending.AfterRefusal(now, delivery.RetryAfter);
        return kept.FirstRefusedAt is { } first && ReportRetrySchedule.GivesUp(first, now) ? null : kept;
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
        string? CallbackPath,
        string? ReleaseName,
        string? ManagerLibraryId,
        WireObject Body,
        long? LibraryId,
        string? Subject,
        IReadOnlyList<string> Files,
        int Refusals = 0,
        DateTimeOffset? FirstRefusedAt = null,
        DateTimeOffset? NotBefore = null)
    {
        /// <summary>This report after the manager answered that it cannot take it yet: when it was first refused, and the earliest it is sent again.</summary>
        public PendingReport AfterRefusal(DateTimeOffset now, TimeSpan? retryAfter)
        {
            var refusals = Refusals + 1;
            return this with { Refusals = refusals, FirstRefusedAt = FirstRefusedAt ?? now, NotBefore = now + ReportRetrySchedule.Wait(refusals, retryAfter) };
        }

        public string ToJson()
        {
            var json = new WireObject()
                .Set("callback_path", CallbackPath)
                .Set("release_name", ReleaseName)
                .Set("manager_library_id", ManagerLibraryId)
                .Set("body", Body)
                .Set("library_id", LibraryId)
                .Set("relative_media_path", Subject)
                .Set("relative_media_paths", HandoffOutputFiles.ToJson(Files));
            if (Refusals > 0)
            {
                json.Set("refusals", Refusals)
                    .Set("first_refused_at", WireTime(FirstRefusedAt))
                    .Set("not_before", WireTime(NotBefore));
            }

            return WireJsonWriter.Dumps(json, WireJsonFormat.Compact);
        }

        private static string? WireTime(DateTimeOffset? at) => at is { } value ? Timestamp.FromDateTimeOffset(value).ToWireText() : null;

        private static DateTimeOffset? ReadTime(WireValue? value) =>
            value is WireString text && DateTimeOffset.TryParse(text.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

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
                    files,
                    dict.Get("refusals") is WireInteger refusals ? (int)refusals.Value : 0,
                    ReadTime(dict.Get("first_refused_at")),
                    ReadTime(dict.Get("not_before")));
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
