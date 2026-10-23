using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>The fixed-shape result dictionaries a pass returns, and the fingerprint guard behind them.</summary>
public sealed partial class RemuxPassRunner
{
    /// <summary>Throws when the source's fingerprint changed during the pass, so a staged output is never published from
    /// a source that moved underneath it.</summary>
    private void AssertSourceUnchanged(string path, SourceFingerprint expected)
    {
        SourceFingerprint current;
        try
        {
            current = SourceFiles.Fingerprint(path, Files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MediaCompletenessException(
                $"The source could not be rechecked after processing ({exception.Message}), so the staged output was not published.",
                exception);
        }

        if (current != expected)
        {
            throw new MediaCompletenessException(
                "The source changed while Weir was reading it. The staged output was discarded and Weir will wait " +
                "for the downloader or importer to finish.");
        }
    }

    /// <summary>
    /// Records what the pass read and what it wrote, the moment the output is in place and before any source cleanup, so every
    /// writer (mkvmerge, ffmpeg, the cover-art ffmpeg fallback, an unchanged copy) and every cleanup choice, kept original or
    /// removed, leaves the same two figures for Activity and the overview totals.
    /// </summary>
    private void RecordSizes(WireObject output, PassContext context, string outputFile)
    {
        long written;
        try
        {
            written = Files.GetLength(outputFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        output.Set("source_size_bytes", context.Expected.SizeBytes);
        output.Set("output_size_bytes", written);
    }

    /// <summary>The result of a pass that failed before execution.</summary>
    public static WireObject FailBefore(string relativeMediaPath, string reason, string? inspectedSourcePath = null, WireObject? extra = null)
    {
        var result = new WireObject()
            .Set("ok", false)
            .Set("outcome", RemuxPassOutcomes.FailedBeforeExecution)
            .Set("preflight_status", "failed")
            .Set("preflight_reason", reason)
            .Set("reason", reason)
            .Set("relative_media_path", relativeMediaPath);
        foreach (var (key, value) in extra?.Items ?? [])
        {
            result.Set(key, value);
        }

        if (!string.IsNullOrEmpty(inspectedSourcePath))
        {
            result.Set("inspected_source_path", inspectedSourcePath);
        }

        return result;
    }

    /// <summary>
    /// The result of a file Weir cannot read at all (ffprobe could not parse it, or found no streams in it): not yet a verdict, because
    /// a download that is still arriving reads the same way. It waits and is looked at again, and only a file that stays unreadable and
    /// unchanged through those looks is refused (<see cref="RemuxPassHandler"/>, <c>SettleUnreadableSourceAsync</c>). What the tool said
    /// stays in <c>technical_detail</c>.
    /// </summary>
    private static WireObject WaitForReadable(string relativeMediaPath, string inspectedSourcePath, string technicalDetail) =>
        SourceNotReady(relativeMediaPath, ToolFailureText.NotReadableYet, inspectedSourcePath)
            .Set("not_ready_kind", UnreadableWait)
            .Set("technical_detail", technicalDetail);

    /// <summary>
    /// Adds what a failed media tool said to <paramref name="result"/> as <c>technical_detail</c>, when the sentence a person reads
    /// is not the tool's own words.
    /// </summary>
    private static WireObject WithTechnicalDetail(WireObject result, Exception exception) =>
        ToolFailureText.Plain(exception) == exception.Message ? result : result.Set("technical_detail", ToolFailureText.Technical(exception));

    /// <summary><c>not_ready_kind</c> of a file that is only waiting out the minimum file age (#632).</summary>
    public const string MinimumAgeWait = "minimum_age";

    /// <summary><c>not_ready_kind</c> of a file Weir could not read from start to finish (#646).</summary>
    public const string UnreadableWait = "unreadable";

    /// <summary>The result of a source that is not ready yet: an expected wait, not a failure.</summary>
    public static WireObject SourceNotReady(string relativeMediaPath, string reason, string? inspectedSourcePath = null)
    {
        var result = new WireObject()
            .Set("ok", false)
            .Set("outcome", RemuxPassOutcomes.SourceNotReady)
            .Set("retryable_wait", true)
            .Set("preflight_status", "waiting")
            .Set("preflight_reason", reason)
            .Set("reason", reason)
            .Set("relative_media_path", relativeMediaPath);
        if (!string.IsNullOrEmpty(inspectedSourcePath))
        {
            result.Set("inspected_source_path", inspectedSourcePath);
        }

        return result;
    }

    /// <summary>The result of a pass whose file left the watched folder: nothing failed, and there is nothing to do.</summary>
    public static WireObject SourceGone(string relativeMediaPath, string? inspectedSourcePath)
    {
        var result = new WireObject()
            .Set("ok", true)
            .Set("outcome", RemuxPassOutcomes.SourceGone)
            .Set("preflight_status", "skipped")
            .Set("preflight_reason", GoneSourceText.Reason)
            .Set("reason", GoneSourceText.Reason)
            .Set("relative_media_path", relativeMediaPath);
        if (!string.IsNullOrEmpty(inspectedSourcePath))
        {
            result.Set("inspected_source_path", inspectedSourcePath);
        }

        return result;
    }

    /// <summary>The result of a pass a guardrail skipped.</summary>
    public static WireObject SkipGuardrail(string relativeMediaPath, string reason, string guardrail, string? inspectedSourcePath, WireObject extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        var result = new WireObject()
            .Set("ok", true)
            .Set("outcome", RemuxPassOutcomes.SkippedGuardrail)
            .Set("preflight_status", "skipped")
            .Set("preflight_reason", reason)
            .Set("reason", reason)
            .Set("guardrail", guardrail)
            .Set("relative_media_path", relativeMediaPath);
        foreach (var (key, value) in extra.Items)
        {
            result.Set(key, value);
        }

        if (inspectedSourcePath is not null)
        {
            result.Set("inspected_source_path", inspectedSourcePath);
        }

        return result;
    }

    private static WireArray StringList(IEnumerable<string> values) => new(values.Select(value => (WireValue)new WireString(value)));

    private static WireValue NullableFloat(double? value) => value is { } v ? new WireNumber(v) : WireNull.Instance;
}
