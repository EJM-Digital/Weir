using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Core.Media;
using Weir.Core.Processing;
using Weir.Core.Rules;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// <see cref="LibraryCleanHandler"/>'s manual-plan concern: turning a person's track choice (from the API's manual
/// plan flow) into a plan for this file, refusing it when the file has changed since they chose.
/// </summary>
public sealed partial class LibraryCleanHandler
{
    /// <summary>A plan built from what a person chose, or the reason Weir would not use their choice.</summary>
    private sealed record ChosenPlan(LibraryFilePlanResult? Plan, (string EventType, string Message)? Problem);

    /// <summary>
    /// Turns one person's track choice into a plan for this file. Their choice names track indices in the file they
    /// were looking at, so a file that has changed since is refused: nothing here could tell which track is which now.
    /// </summary>
    private ChosenPlan PlanFromChoice(ProbeResult probe, ManualPlanChoice choice, WireValue? expectedSize, string path)
    {
        if (expectedSize is WireInteger expected && CurrentSize(path) != (long)expected.Value)
        {
            return new ChosenPlan(null, (LibraryActivityEventTypes.FileFailed,
                "This file changed after its tracks were chosen, so Weir left it alone. Open it again and choose once more."));
        }

        SplitProbeStreams split;
        try
        {
            split = RemuxRules.SplitStreams(probe);
        }
        catch (RulesInputException)
        {
            return new ChosenPlan(null, (LibraryActivityEventTypes.FileFailed, ToolFailureText.UnusableTrackData));
        }

        if (!ManualTrackPlan.TryValidate(choice, ManualTrackPlan.ClassifyIndices(split), out var invalid))
        {
            return new ChosenPlan(null, (LibraryActivityEventTypes.FileFailed, invalid));
        }

        var chosenPlan = ManualTrackPlan.BuildPlan(split, choice);
        if (!RemuxRules.IsRemuxRequired(chosenPlan, split.Audio, split.Subtitles))
        {
            return new ChosenPlan(null, (LibraryActivityEventTypes.FileSkipped,
                "What you chose is what this file already holds, so there was nothing to do."));
        }

        return new ChosenPlan(LibraryFilePlanResult.WouldChange(chosenPlan, LibraryFilePlanner.EstimateSavings(probe, split, chosenPlan)), null);
    }

    /// <summary>The file's size now, or -1 when Weir cannot read it — which never matches a size someone chose against.</summary>
    private long CurrentSize(string path)
    {
        try
        {
            return _files.FileExists(path) ? _files.GetLength(path) : -1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }
}
