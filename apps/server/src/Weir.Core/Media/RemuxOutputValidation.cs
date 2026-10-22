using System.Globalization;
using System.Text.Json;
using Weir.Core.Rules;
using Weir.Core.Text;

namespace Weir.Core.Media;

/// <summary>
/// Issue #500: checks a staged remux output against the whole <see cref="RemuxPlan"/> — the container family, the
/// track type and disposition at every position, the language tags the plan sets, new ffprobe warnings the source
/// did not have, and metadata rules that must have taken effect. <see cref="ProbeOutput.ValidateRemuxOutput"/> is the
/// narrower "at least the expected audio count and not much shorter" check that the golden files cover.
/// Derived from Muxarr's <c>OutputValidator</c> (https://github.com/KirovAir/muxarr, GPL-3.0); see
/// THIRD_PARTY_NOTICES.md.
/// </summary>
/// <remarks>
/// Split by concern: the expected/actual track layout is <c>RemuxOutputValidation.Layout.cs</c>, reading a probe
/// document's container family and title is <c>RemuxOutputValidation.ProbeFields.cs</c>, the new-warnings check is
/// <c>RemuxOutputValidation.Warnings.cs</c>, and the expected-duration check is
/// <c>RemuxOutputValidation.Duration.cs</c>. This file holds only the full check that ties them together.
/// </remarks>
public static partial class RemuxOutputValidation
{
    /// <summary>
    /// The full check (#500 steps 1-6). Throws <see cref="MediaToolException"/> naming exactly what differed for a
    /// structural mismatch (container, counts, position, disposition, language, warnings, metadata), or
    /// <see cref="MediaCompletenessException"/> when the output cannot be confirmed complete (no measurable
    /// duration, or a real shortfall) — both are execution failures, never content rejections, so a bad remux never
    /// tells a library's reject policy the release itself is bad.
    /// </summary>
    public static void ValidateAgainstPlan(
        JsonElement outputProbe,
        RemuxPlan plan,
        string? sourceFormatName,
        double expectedDurationSeconds,
        IReadOnlyList<string> sourceWarnings,
        IReadOnlyList<string> outputWarnings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceWarnings);
        ArgumentNullException.ThrowIfNull(outputWarnings);

        // 1. Container family.
        var outputFormatName = FormatName(outputProbe);
        var sourceFamily = ContainerFamily(sourceFormatName);
        var outputFamily = ContainerFamily(outputFormatName);
        if (sourceFamily.Length > 0 && outputFamily.Length > 0 && sourceFamily != outputFamily)
        {
            throw Mismatch(
                $"Planned to keep the {sourceFamily} container, but the output is {outputFamily} " +
                $"({(outputFormatName is { Length: > 0 } name ? name : "unknown")}).");
        }

        var expected = ExpectedLayout(plan);
        var actual = ActualLayout(outputProbe);

        // 2. Track counts, per type — a clearer message than a bare position count for the common case (a dropped subtitle).
        foreach (var type in new[] { "video", "audio", "subtitle" })
        {
            var wantCount = expected.Count(t => t.CodecType == type);
            var gotCount = actual.Count(t => t.CodecType == type);
            if (wantCount != gotCount)
            {
                throw Mismatch(
                    $"Planned {Plural.Of(wantCount, type + " track")}, output has {gotCount.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        if (actual.Count != expected.Count)
        {
            throw Mismatch(
                $"Planned {Plural.Of(expected.Count, "track")}, output has {actual.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        // 3. Track type, disposition and language at every position.
        for (var i = 0; i < expected.Count; i++)
        {
            var want = expected[i];
            var got = actual[i];
            var position = i.ToString(CultureInfo.InvariantCulture);
            if (want.CodecType != got.CodecType)
            {
                throw Mismatch($"Planned output position {position} to be {want.CodecType}, output has {got.CodecType}.");
            }

            if (want.Default is { } wantDefault && wantDefault != got.Default)
            {
                throw Mismatch(
                    $"Planned the {want.CodecType} track at position {position} to have default={BoolText(wantDefault)}, output has default={BoolText(got.Default)}.");
            }

            if (want.Forced is { } wantForced && wantForced != got.Forced)
            {
                throw Mismatch(
                    $"Planned the {want.CodecType} track at position {position} to have forced={BoolText(wantForced)}, output has forced={BoolText(got.Forced)}.");
            }

            if (want.Language is { Length: > 0 } wantLanguage)
            {
                var gotLanguage = RemuxRules.NormalizeLang(got.Language);
                if (gotLanguage != wantLanguage)
                {
                    throw Mismatch(
                        $"Planned the {want.CodecType} track at position {position} to be tagged '{wantLanguage}', output is tagged " +
                        $"'{(gotLanguage.Length > 0 ? gotLanguage : "und")}'.");
                }
            }
        }

        // 4. Duration, against the max of the kept source streams (or a direct measurement), not the whole source.
        var outputDuration = ProbeOutput.DurationSeconds(outputProbe);
        if (outputDuration is null)
        {
            throw new MediaCompletenessException(
                "Validation failed: Weir could not confirm the staged output duration, so it was not published.");
        }

        var tolerance = Math.Max(0.5, expectedDurationSeconds * 0.01);
        if (outputDuration.Value < expectedDurationSeconds - tolerance)
        {
            throw new MediaCompletenessException(
                "Validation failed: the staged output is incomplete "
                + $"({MediaText.FormatFixed(outputDuration.Value, 1)}s of {MediaText.FormatFixed(expectedDurationSeconds, 1)}s expected), so it was not published.");
        }

        // 5. New ffprobe warnings.
        var newWarnings = WarningsNewInOutput(sourceWarnings, outputWarnings);
        if (newWarnings.Count > 0)
        {
            throw Mismatch(
                "ffprobe reported warnings on the output that the source did not have: " + string.Join(" | ", newWarnings));
        }

        // 6. Metadata: a cleared title must actually be cleared. Chapter removal (#498) is not checked here.
        if (plan.Metadata.RemoveTitle)
        {
            var outputTitle = FormatTitle(outputProbe);
            if (!string.IsNullOrEmpty(outputTitle))
            {
                throw Mismatch($"Planned to clear the container title, output still has '{outputTitle}'.");
            }
        }
    }

    /// <summary>A way the output differs from the plan: named exactly for the log, worded for a person as a copy that was not published.</summary>
    private static MediaToolException Mismatch(string message) =>
        new(message) { PlainMessage = ToolFailureText.OutputNotPublishable };

    private static string BoolText(bool value) => value ? "True" : "False";
}
