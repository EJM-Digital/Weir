using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassRunner
{
    private async Task<WireObject> PlaceUnchangedAsync(
        PassContext context,
        WireObject output,
        RemuxPlan plan,
        IReadOnlyList<string> argv,
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
        var passThrough = request.PassThroughUnchanged;
        var report = request.ProgressReporter;
        var relativeMediaPath = context.RelativeMediaPath;
        output.Set("outcome", RemuxPassOutcomes.LiveSkippedNotRequired);
        output.Set("processing_output_folder_resolved", context.OutputDirectory);
        if (passThrough)
        {
            output.Set("after_track_lines_meaning", "No audio, subtitle, or metadata rules were applied because the operator chose Pass through unchanged.");
            output.Set("reason",
                "The operator bypassed the rules for this edge case. Weir validated and placed the unchanged file " +
                "in the output folder before running normal post-success source cleanup.");
        }
        else
        {
            output.Set("after_track_lines_meaning", "No ffmpeg run was needed because the file already matched the saved rules.");
            output.Set("reason", "The file already matched the saved rules, so Weir placed it in the output folder without rewriting it.");
        }

        var finalSkip = Path.Join(context.OutputDirectory, relative);
        var disk = FileLifecycle.CheckMinimumFreeDiskSpace(finalSkip, request.MinimumFreeDiskSpaceMb, FreeBytes);
        if (!disk.Ok)
        {
            return OutputDriveWait(disk, context, output);
        }

        // The unchanged-copy path collides exactly like the remux path does.
        var collision = OutputCollision.Decide(finalSkip, context.Source, context.Source, collisionPolicy);
        var copyStarted = _time.GetTimestamp();
        var lastPercent = -1.0;
        var lastAt = copyStarted;
        void ReportCopyProgress(long copied, long total)
        {
            if (report is null)
            {
                return;
            }

            var now = _time.GetTimestamp();
            var elapsed = Math.Max(0.001, _time.GetElapsedTime(copyStarted, now).TotalSeconds);
            var percent = total <= 0 ? 100.0 : Math.Min(100.0, copied * 100.0 / total);
            if (percent < 100.0 && percent - lastPercent < 1.0 && _time.GetElapsedTime(lastAt, now).TotalSeconds < 2.0)
            {
                return;
            }

            lastPercent = percent;
            lastAt = now;
            var bytesPerSecond = copied / elapsed;
            var remaining = Math.Max(0, total - copied);
            report(new WireObject()
                .Set("status", "processing")
                .Set("stage", PassStages.Writing)
                .Set("percent", percent)
                .Set("eta_seconds", bytesPerSecond > 0 ? WireValue.Of(remaining / bytesPerSecond) : WireNull.Instance)
                .Set("elapsed_seconds", elapsed)
                .Set("relative_media_path", relativeMediaPath)
                .Set("inspected_source_path", context.Inspected)
                .Set("media_scope", context.Scope)
                .Set("processed_bytes", copied)
                .Set("total_bytes", total)
                .Set("bytes_read", copied)
                .Set("bytes_written", copied)
                .Set("speed", $"{(bytesPerSecond / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture)} MB/s")
                .Set("message", passThrough
                    ? "Weir is passing this file through unchanged."
                    : "Weir is copying the unchanged file to the output folder."));
        }

        bool replacedExisting;
        string method;
        try
        {
            if (collision.Wrote)
            {
                finalSkip = collision.Destination;
                (replacedExisting, method) = await CopyUnchangedSourceToOutputAsync(context, finalSkip, ReportCopyProgress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (replacedExisting, method) = (false, "skipped_by_collision_policy");
            }
        }
        catch (MediaCompletenessException exception)
        {
            return WithTechnicalDetail(SourceNotReady(relativeMediaPath, ToolFailureText.Plain(exception), context.Inspected), exception);
        }
#pragma warning disable CA1031 // Any failure while copying becomes this file's recorded failure; the worker survives it.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            report?.Invoke(new WireObject()
                .Set("status", "failed")
                .Set("percent", WireNull.Instance)
                .Set("eta_seconds", WireNull.Instance)
                .Set("relative_media_path", relativeMediaPath)
                .Set("inspected_source_path", context.Inspected)
                .Set("media_scope", context.Scope)
                .Set("message", "Weir could not copy this unchanged file to the output folder.")
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
                .Set("processing_output_folder_resolved", context.OutputDirectory)
                .Set("stream_counts", output["stream_counts"])
                .Set("plan_summary", output["plan_summary"])
                .Set("audio_before", audioBefore)
                .Set("audio_after", audioAfter)
                .Set("subs_before", subsBefore)
                .Set("subs_after", subsAfter)
                .Set("remux_required", false)
                .Set("ffmpeg_argv", StringList(argv))
                .Set("audio_selection_notes", StringList(plan.AudioSelectionNotes)), exception);
        }

        output.Set("output_file", RemuxPassPaths.Resolve(finalSkip));
        output.Set("output_replaced_existing", replacedExisting);
        output.Set("output_collision_policy", collision.Policy);
        output.Set("output_collision_action", collision.Action);
        output.Set("output_collision_reason", collision.Reason);
        await _facts.RecordOutputCollisionAsync(relativeMediaPath, collision, request.LibraryId, cancellationToken).ConfigureAwait(false);
        output.Set("output_copied_without_remux", true);
        output.Set("unchanged_output_method", method);
        output.Set("live_mutations_skipped", false);
        if (collision.Wrote)
        {
            RecordSizes(output, context, finalSkip);
        }

        await MigrateSidecarsBeforeCleanupAsync(context.Source, finalSkip, sidecarPatterns, request.Runtime.PreserveOriginalTimestamps, output).ConfigureAwait(false);
        await HandleCleanupAfterSuccessAsync(context, output, finalSkip, cancellationToken).ConfigureAwait(false);
        try
        {
            output.Set("unchanged_output_method", await DetachHardlinkedOutputIfSourceRemainsAsync(context, finalSkip, method, ReportCopyProgress).ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The source stays put when the output cannot be detached; the file is failed, not the worker.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            FileLifecycle.BestEffortDelete(finalSkip);
            output.Set("ok", false);
            output.Set("outcome", RemuxPassOutcomes.FailedDuringExecution);
            output.Set("reason",
                "Weir preserved the watched source because cleanup could not finish, but could not detach the " +
                $"validated output from that source safely. The unsafe output link was removed. The system reported: {exception.Message}");
            output.Remove("output_file");
            return output;
        }

        ReportStage(report, relativeMediaPath, "finishing", PassStages.Verifying, "The file was placed. Weir is doing final safety checks.");
        await RunScopeOutputCleanupAsync(context, output, finalSkip, cancellationToken).ConfigureAwait(false);
        ReportStage(report, relativeMediaPath, "finishing", PassStages.HandingBack, "Weir is handing the finished file back.");
        return output;
    }

    /// <summary>Publishes an unchanged source: the Windows hard link when it can be one, else a validated copy.</summary>
    private async Task<(bool ReplacedExisting, string Method)> CopyUnchangedSourceToOutputAsync(PassContext context, string final, Action<long, long> progress, CancellationToken cancellationToken)
    {
        var src = context.Source;
        var replacedExisting = File.Exists(final) || Directory.Exists(final);
        if (replacedExisting && RemuxPassPaths.SamePath(RemuxPassPaths.Resolve(final), src))
        {
            throw new InvalidOperationException("The output path resolves to the watched source file; output and watched folders must differ.");
        }

        async Task ValidateStaged(string staged)
        {
            await _tools.ValidateStagedOutputAsync(staged, src, context.SourceProbe, context.Plan, context.SourceWarnings, cancellationToken).ConfigureAwait(false);
            AssertSourceUnchanged(src, context.Expected);
        }

        // Windows' held source handle denies writers for the complete pass, which makes a staged hard link safe and turns a
        // same-volume no-change file into metadata work. Elsewhere locks are advisory, so the independent copy stays.
        if (HardlinkFastPathSupported && await FileLifecycle.TryHardlinkToFinalAsync(src, final, ValidateStaged, _ownership).ConfigureAwait(false))
        {
            var size = new FileInfo(src).Length;
            progress(size, size);
            return (replacedExisting, "validated_hardlink");
        }

        await FileLifecycle.SafeCopyToFinalAsync(src, final, ValidateStaged, progress, ownership: _ownership, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (replacedExisting, "validated_copy");
    }

    /// <summary>
    /// When a safety gate kept the watched source, breaks the shared file so a
    /// later writer cannot change the published output through the watched name.
    /// </summary>
    private async Task<string> DetachHardlinkedOutputIfSourceRemainsAsync(PassContext context, string final, string method, Action<long, long> progress)
    {
        if (method != "validated_hardlink" || !File.Exists(context.Source))
        {
            return method;
        }

        await FileLifecycle.SafeCopyToFinalAsync(
            final,
            final,
            async staged =>
            {
                await _tools.ValidateStagedOutputAsync(staged, context.Source, context.SourceProbe, context.Plan, context.SourceWarnings).ConfigureAwait(false);
                AssertSourceUnchanged(context.Source, context.Expected);
            },
            progress,
            ownership: _ownership).ConfigureAwait(false);
        return "validated_hardlink_detached_copy";
    }
}
