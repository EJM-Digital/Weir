using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;
using Weir.Infrastructure.Runtime;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassRunner
{
    private async Task<WireObject> RemuxAndPublishAsync(
        PassContext context,
        WireObject output,
        RemuxPlan plan,
        IReadOnlyList<string> argv,
        IReadOnlyList<string> ffmpegInputFlags,
        string audioBefore,
        string audioAfter,
        string subsBefore,
        string subsAfter,
        string relative,
        string collisionPolicy,
        IReadOnlyList<string> sidecarPatterns,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var report = request.ProgressReporter;
        var relativeMediaPath = context.RelativeMediaPath;
        var src = context.Source;
        var workDir = request.Runtime.WorkFolderEffective;
        string final;
        CollisionDecision collision;
        bool replacedExisting;
        try
        {
            var workDisk = FileLifecycle.CheckMinimumFreeDiskSpace(Path.Join(workDir, $".{Path.GetFileName(src)}.work-preflight"), request.MinimumFreeDiskSpaceMb, FreeBytes);
            if (!workDisk.Ok)
            {
                return WorkFolderDriveWait(workDisk, context, output);
            }

            report?.Invoke(new WireObject()
                .Set("status", "processing")
                .Set("stage", PassStages.Writing)
                .Set("percent", 0.0)
                .Set("eta_seconds", WireNull.Instance)
                .Set("elapsed_seconds", 0L)
                .Set("relative_media_path", relativeMediaPath)
                .Set("inspected_source_path", context.Inspected)
                .Set("media_scope", context.Scope)
                .Set("stream_counts", output["stream_counts"])
                .Set("duration_seconds", NullableFloat(context.Duration))
                // What comes out, so Live can say it while the file is being written, not only afterwards.
                .Set("removed_audio", output["removed_audio"])
                .Set("removed_subtitles", output["removed_subtitles"])
                .Set("message", "Weir has started writing the cleaned-up file."));
            // The staged output keeps the source's own extension (RemuxToTempFileAsync), so the source
            // path is what decides whether mkvmerge can take it.
            var writer = _tools.WriterFor(src, request.Runtime.RemuxWriter);
            var staged = await _tools.RemuxToTempFileAsync(
                src,
                workDir,
                plan,
                context.SourceProbe,
                context.SourceWarnings,
                report is null
                    ? null
                    : update => report(ProgressWithUpdate(
                        new WireObject()
                            .Set("status", "processing")
                            .Set("stage", PassStages.Writing)
                            .Set("relative_media_path", relativeMediaPath)
                            .Set("inspected_source_path", context.Inspected)
                            .Set("media_scope", context.Scope)
                            .Set("stream_counts", output["stream_counts"])
                            .Set("duration_seconds", NullableFloat(context.Duration))
                            .Set("removed_audio", output["removed_audio"])
                            .Set("removed_subtitles", output["removed_subtitles"])
                            .Set("message", "Weir is writing the cleaned-up file."),
                        update,
                        context.Expected.SizeBytes)),
                context.Duration,
                ffmpegInputFlags,
                writer,
                request.KeepFailedWorkFiles,
                cancellationToken).ConfigureAwait(false);
            var tmp = staged.Path;
            try
            {
                AssertSourceUnchanged(src, context.Expected);
            }
            catch (MediaCompletenessException)
            {
                FileLifecycle.BestEffortDelete(tmp);
                throw;
            }

            final = Path.Join(context.OutputDirectory, relative);
            var outputDisk = FileLifecycle.CheckMinimumFreeDiskSpace(final, request.MinimumFreeDiskSpaceMb, FreeBytes);
            if (!outputDisk.Ok)
            {
                FileLifecycle.BestEffortDelete(tmp);
                return OutputDriveWait(outputDisk, context, output);
            }

            // The collision policy and the decision are both recorded, so a collision is never silent (#349).
            collision = OutputCollision.Decide(final, src, tmp, collisionPolicy);
            replacedExisting = collision.ReplacedExisting;
            if (collision.Wrote)
            {
                final = collision.Destination;
                await BlockingWork.RunAsync(() => FileLifecycle.SafeFinalizeFile(tmp, final, _ownership)).ConfigureAwait(false);
            }
        }
        catch (MediaCompletenessException exception)
        {
            report?.Invoke(new WireObject()
                .Set("status", "waiting")
                .Set("percent", WireNull.Instance)
                .Set("eta_seconds", WireNull.Instance)
                .Set("relative_media_path", relativeMediaPath)
                .Set("inspected_source_path", context.Inspected)
                .Set("media_scope", context.Scope)
                .Set("message", "Weir is waiting for this file to finish downloading.")
                .Set("reason", ToolFailureText.Plain(exception)));
            return WithTechnicalDetail(SourceNotReady(relativeMediaPath, ToolFailureText.Plain(exception), context.Inspected), exception);
        }
#pragma warning disable CA1031 // Any failure while writing becomes this file's recorded failure; the worker survives it.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            report?.Invoke(new WireObject()
                .Set("status", "failed")
                .Set("percent", WireNull.Instance)
                .Set("eta_seconds", WireNull.Instance)
                .Set("relative_media_path", relativeMediaPath)
                .Set("inspected_source_path", context.Inspected)
                .Set("media_scope", context.Scope)
                .Set("message", "Weir could not finish this file.")
                .Set("reason", ToolFailureText.Plain(exception)));
            return WithTechnicalDetail(new WireObject()
                .Set("ok", false)
                .Set("outcome", RemuxPassOutcomes.FailedDuringExecution)
                .Set("preflight_status", "ok")
                .Set("preflight_reason", "ffprobe completed and remux plan was evaluated")
                .Set("reason", ToolFailureText.Plain(exception))
                .Set("relative_media_path", relativeMediaPath)
                .Set("inspected_source_path", context.Inspected)
                .Set("processing_watched_folder_resolved", context.WatchedRoot)
                .Set("stream_counts", output["stream_counts"])
                .Set("plan_summary", output["plan_summary"])
                .Set("audio_before", audioBefore)
                .Set("audio_after", audioAfter)
                .Set("subs_before", subsBefore)
                .Set("subs_after", subsAfter)
                .Set("remux_required", true)
                .Set("ffmpeg_argv", StringList(argv))
                .Set("audio_selection_notes", StringList(plan.AudioSelectionNotes))
                .Set("after_track_lines_meaning", "Remux failed partway; lines above were computed before ffmpeg — output file was not committed."), exception);
        }

        var resolvedFinal = RemuxPassPaths.Resolve(final);
        output.Set("output_file", resolvedFinal);
        output.Set("output_replaced_existing", replacedExisting);
        output.Set("processing_output_folder_resolved", context.OutputDirectory);
        output.Set("output_collision_policy", collision.Policy);
        output.Set("output_collision_action", collision.Action);
        // For the person asking "why is there no new output for this file".
        output.Set("output_collision_reason", collision.Reason);
        await _facts.RecordOutputCollisionAsync(relativeMediaPath, collision, context.Request.LibraryId, cancellationToken).ConfigureAwait(false);
        if (!collision.Wrote || replacedExisting)
        {
            output.Set("output_replacement_note", collision.Reason);
        }

        if (collision.Wrote)
        {
            RecordSizes(output, context, final);
        }

        output.Set("after_track_lines_meaning",
            "Live remux finished; before = source probe; after = planned disposition (copy remux — " +
            "ffprobe of the written file was used for validation only).");
        try
        {
            var saved = Files.GetLength(src) - Files.GetLength(final);
            if (saved > 0)
            {
                _metrics?.RecordModuleSavings("processing", saved);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        report?.Invoke(new WireObject()
            .Set("status", "finishing")
            .Set("stage", PassStages.Verifying)
            .Set("percent", 100.0)
            .Set("eta_seconds", 0L)
            .Set("relative_media_path", relativeMediaPath)
            .Set("inspected_source_path", context.Inspected)
            .Set("output_file", resolvedFinal)
            .Set("media_scope", context.Scope)
            .Set("removed_audio", output["removed_audio"])
            .Set("removed_subtitles", output["removed_subtitles"])
            .Set("message", "The cleaned-up file was written. Weir is doing final safety checks."));
        await MigrateSidecarsBeforeCleanupAsync(src, final, sidecarPatterns, request.Runtime.PreserveOriginalTimestamps, output).ConfigureAwait(false);
        await HandleCleanupAfterSuccessAsync(context, output, final, cancellationToken).ConfigureAwait(false);
        await RunScopeOutputCleanupAsync(context, output, final, cancellationToken).ConfigureAwait(false);
        ReportStage(report, relativeMediaPath, "finishing", PassStages.HandingBack, "Weir is handing the finished file back.");
        report?.Invoke(new WireObject()
            .Set("status", "finished")
            .Set("percent", 100.0)
            .Set("eta_seconds", 0L)
            .Set("relative_media_path", relativeMediaPath)
            .Set("inspected_source_path", context.Inspected)
            .Set("output_file", resolvedFinal)
            .Set("media_scope", context.Scope)
            .Set("message", "Finished processing this file."));
        return output;
    }

    /// <summary>
    /// <c>{**base, **update}</c> for a progress block, with how much has been read and written so far for the System view's
    /// throughput. The tools copy tracks, so the share of the source read is its percent; ffmpeg says how much it has
    /// written, and for mkvmerge, which does not, the output is about as large as what was read.
    /// </summary>
    private static WireObject ProgressWithUpdate(WireObject body, FfmpegProgressUpdate update, long sourceSizeBytes)
    {
        var read = update.Percent is { } percent ? (long)(sourceSizeBytes * percent / 100d) : (long?)null;
        return body
            .Set("percent", NullableFloat(update.Percent))
            .Set("eta_seconds", update.EtaSeconds)
            .Set("elapsed_seconds", update.ElapsedSeconds)
            .Set("processed_seconds", NullableFloat(update.ProcessedSeconds))
            .Set("speed", update.Speed)
            .Set("progress", update.Progress)
            .Set("bytes_read", read)
            .Set("bytes_written", update.TotalSizeBytes ?? read);
    }
}
