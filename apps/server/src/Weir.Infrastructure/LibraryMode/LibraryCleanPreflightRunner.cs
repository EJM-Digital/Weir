using Weir.Core.LibraryMode;
using Weir.Core.MediaManagers;
using Weir.Core.Rules;

namespace Weir.Infrastructure.LibraryMode;

/// <summary>
/// #508's preflight over already-scanned files, shared by Clean on the Library screen and by the scheduled scan and clean,
/// so a file is judged the same way whoever asked for it to be cleaned.
/// </summary>
public static class LibraryCleanPreflightRunner
{
    /// <summary>
    /// #508's hardlink preflight (step 1) for a set of already-scanned files, run when the clean is asked for rather than
    /// trusting the scan's own cached classification, since a download client can start seeding a file at any moment
    /// after it was scanned. The re-download-risk half (step 2, #551) runs too, for any file a scan matched to a
    /// Sonarr/Radarr title: the match already carries the manager's file id and quality profile id, so no extra lookup is
    /// needed beyond the two calls <see cref="RedownloadRiskChecker"/> itself makes. A file with nothing removed, or no
    /// match, never dials out.
    /// </summary>
    public static async Task<List<LibraryFilePreflightResult>> RunAsync(
        IEnumerable<LibraryScanFileEntry> files,
        LibrarySettings settings,
        ProcessingRulesConfig rules,
        string mediaScope,
        IHardlinkInspector inspector,
        RedownloadRiskChecker riskChecker,
        IReadOnlyDictionary<long, ManagerConnection> connectionsById,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inspector);
        var results = new List<LibraryFilePreflightResult>();
        foreach (var file in files)
        {
            int? linkCount;
            try
            {
                linkCount = inspector.LinkCount(file.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                linkCount = null;
            }

            var hardlink = HardlinkPolicy.Evaluate(linkCount, settings.CleanHardlinkedFiles);
            var risk = await RedownloadRiskForFileAsync(file, rules, mediaScope, riskChecker, connectionsById, settings.SkipIfManagerWouldRedownload, cancellationToken).ConfigureAwait(false);
            results.Add(LibraryCleanPreflight.Evaluate(file.Path, hardlink, risk));
        }

        return results;
    }

    /// <summary>The skipped files' reasons as the confirmation dialog and the clean's answer word them.</summary>
    public static List<string> WarningMessages(IEnumerable<LibraryFilePreflightResult> preflight) =>
        preflight.Where(r => r.Skip).Select(r => $"{Path.GetFileName(r.FilePath)}: {string.Join(" ", r.SkipReasons)}").ToList();

    private static async Task<RedownloadRiskAssessment?> RedownloadRiskForFileAsync(
        LibraryScanFileEntry file,
        ProcessingRulesConfig rules,
        string mediaScope,
        RedownloadRiskChecker riskChecker,
        IReadOnlyDictionary<long, ManagerConnection> connectionsById,
        bool skipIfManagerWouldRedownload,
        CancellationToken cancellationToken)
    {
        if (file.ManagerConnectionId is not { } connectionId ||
            file.ManagerFileId is not { } fileId ||
            file.ManagerQualityProfileId is not { } qualityProfileId ||
            !connectionsById.TryGetValue(connectionId, out var connection))
        {
            return null;
        }

        var removedAudioLanguages = RemovedAudioLanguages(file, rules);
        if (removedAudioLanguages.Count == 0)
        {
            return null;
        }

        return await riskChecker.CheckAsync(
                connection, mediaScope, fileId, qualityProfileId, file.ManagerTitle ?? Path.GetFileName(file.Path),
                removedAudioLanguages, skipIfManagerWouldRedownload, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The languages of the audio tracks the current rules would remove from an already-scanned file, re-derived
    /// from its cached ffprobe JSON (the scan's own plan cache carries only a count, not the languages) — the same
    /// replan <see cref="LibraryCleanHandler"/> runs before actually touching the file.
    /// </summary>
    internal static List<string> RemovedAudioLanguages(LibraryScanFileEntry file, ProcessingRulesConfig rules)
    {
        if (file.RemovedAudioCount == 0 || file.ProbeJson is not { Length: > 0 } probeJson)
        {
            return [];
        }

        try
        {
            var classification = LibraryFilePlanner.Classify(ProbeResult.Parse(probeJson), rules);
            return classification.Plan?.RemovedTrackRecords
                .Where(track => track.Type == RemovedTrackType.Audio)
                .Select(track => track.Language)
                .ToList() ?? [];
        }
        catch (Exception exception) when (exception is RulesInputException or System.Text.Json.JsonException)
        {
            // A cached probe that cannot be read says nothing about what would be removed.
            return [];
        }
    }
}
