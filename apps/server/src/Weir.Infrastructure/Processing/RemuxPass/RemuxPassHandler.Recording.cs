using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    /// <summary>
    /// Writes the processing record and the Activity row together so they cannot disagree. A progress row
    /// already started for the pass becomes the completed row. With <paramref name="newEntry"/> false (the file is already listed as
    /// gone) the processing record is still written and a started progress row still finished, but no entry is added.
    /// </summary>
    internal async Task RecordAsync(WireObject payload, long? activityId = null, bool newEntry = true)
    {
        var detail = RemuxPassVisibility.ActivityDetail(payload);
        var title = RemuxPassVisibility.ActivityTitle(payload);
        try
        {
            await LockedWrites.RunAsync(
                _database,
                async uow =>
                {
                    if (payload.Get("relative_media_path") is WireString rel && WireStrings.Strip(rel.Value).Length > 0)
                    {
                        try
                        {
                            await RemuxPassFileState.RecordFileLogAsync(uow, rel.Value, title, payload, _time.GetUtcNow()).ConfigureAwait(false);
                        }
                        catch (SqliteException exception) when (LockedWrites.IsLock(exception))
                        {
                            throw;
                        }
                        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
                        {
                            _logger.LogError(exception, "Weir could not write the processing record for {Path}.", rel.Value);
                        }
                    }

                    if (activityId is { } id &&
                        await SqliteActivityWriter.UpdateAsync(uow, id, ActivityEventTypes.ProcessingFileRemuxPassCompleted, title, detail).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (!newEntry)
                    {
                        return;
                    }

                    await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(ActivityEventTypes.ProcessingFileRemuxPassCompleted, "processing", title, detail))
                        .ConfigureAwait(false);
                },
                _logger,
                "processing record").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            // Activity is observability, never a prerequisite for a safe media mutation.
            _logger.LogWarning(exception, "Weir could not save the processing record; the pass result remains authoritative.");
        }
    }

    /// <summary>
    /// Applies an opt-in rejection deletion, after the rejection was recorded. Only a rejection the pass itself made reaches it: the
    /// pass names the watched folder and the file it looked at in every such result, and the deletion is only ever chosen for one.
    /// </summary>
    private async Task FinishRejectedInputCleanupAsync(WireObject result, ProcessingLibraryRecord library, long? libraryId, string mediaScope, WireObject? origin)
    {
        if (result.Get("rejected_file_action") is not WireString { Value: RejectedFileActions.DeleteFile })
        {
            return;
        }

        var cleanup = RemuxPassPaths.CleanupRejectedFile(
            WireConvert.Str(result["processing_watched_folder_resolved"]), WireConvert.Str(result["inspected_source_path"]), "delete_file");
        result.Set("rejected_cleanup_status", cleanup.Deleted ? "deleted" : "not_deleted");
        result.Set("rejected_cleanup_detail", cleanup.Deleted && WeirOnlyRejection.Applies(library, origin) ? WeirOnlyRejection.Deleted : cleanup.Detail);
        await ApplyFileOutcomeStateAsync(result, libraryId, mediaScope, origin).ConfigureAwait(false);
        await LockedWrites.RunAsync(
            _database,
            uow => RemuxPassFileState.RecordFileLogAsync(uow, WireConvert.Str(result["relative_media_path"]), "Rejected file cleanup finished", result, _time.GetUtcNow()),
            _logger,
            "rejected file cleanup record").ConfigureAwait(false);
    }
}
