using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.Logs;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.SystemLog;

/// <summary>A background job kind in words, and what it is about.</summary>
public sealed record JobKindRule(string Kind, string Label, string Category);

/// <summary>
/// How every row of System › Logs gets its level and its category: the one place those are decided. The lists are read
/// both here, for a row in hand, and by <see cref="SystemLogSql"/>, which turns them into the same decision inside a
/// query, so a count and the row it counts can never disagree.
/// </summary>
public static class SystemLogRules
{
    /// <summary>
    /// Activity event types by what they are about; the first prefix that matches wins, and an event no prefix claims is
    /// about Weir itself. Event types are an append-only contract, so a prefix names a family, never a single event.
    /// </summary>
    public static readonly IReadOnlyList<(string Prefix, string Category)> EventTypeCategories =
    [
        ("auth.", SystemLogCategories.SignIn),
        ("arr_library.", SystemLogCategories.Connections),
        ("system.reconciliation.", SystemLogCategories.Connections),
        ("library.scan_", SystemLogCategories.Scans),
        ("library.file_change_notif", SystemLogCategories.Connections),
        ("library.", SystemLogCategories.Library),
        ("processing.handoff_", SystemLogCategories.Connections),
        ("processing.handback_outcome", SystemLogCategories.Connections),
        ("processing.downloaded_scan_", SystemLogCategories.Connections),
        ("processing.unclaimed_handback_cleanup", SystemLogCategories.Cleanup),
        ("processing.work_temp_stale_sweep", SystemLogCategories.Cleanup),
        ("processing.failure_cleanup_sweep", SystemLogCategories.Cleanup),
        ("processing.file_removal_", SystemLogCategories.Cleanup),
        ("processing.file_left_watched_folder", SystemLogCategories.Scans),
        ("processing.", SystemLogCategories.Processing),
    ];

    /// <summary>How an Activity event's recorded result reads as a level; a result not listed (skipped, running, none) is information.</summary>
    public static readonly IReadOnlyDictionary<string, string> EventResultLevels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["failed"] = SystemLogLevels.Error,
        ["warning"] = SystemLogLevels.Warning,
        ["retrying"] = SystemLogLevels.Warning,
        ["success"] = SystemLogLevels.Success,
    };

    /// <summary>The job kinds Weir runs. A kind not listed reads as its own words and counts as Weir's.</summary>
    public static readonly IReadOnlyList<JobKindRule> JobKinds =
    [
        new(ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch, "Check watched folders", SystemLogCategories.Scans),
        new(LibraryModeJobKinds.ScanKind, "Scan a library", SystemLogCategories.Scans),
        new(LibraryModeJobKinds.CleanKind, "Clean a library file", SystemLogCategories.Library),
        new(IntakeRules.RemuxPassJobKind, "Process a media file", SystemLogCategories.Processing),
        new(IntakeRules.PassThroughJobKind, "Hand a file back unchanged", SystemLogCategories.Processing),
        new(IntakeRules.RejectJobKind, "Reject a file", SystemLogCategories.Processing),
        new(PeriodicJobKinds.WorkTempStaleSweep, "Clean temporary work files", SystemLogCategories.Cleanup),
        new(PeriodicJobKinds.UnclaimedHandbackCleanup, "Remove copies nobody picked up", SystemLogCategories.Cleanup),
        new(DownloadedScanRetryHandler.Kind, "Ask a media manager to scan again", SystemLogCategories.Connections),
    ];

    /// <summary>
    /// How a job's status reads as a level, first match first. A job waiting to try again after a failure is a warning;
    /// otherwise a job is information while it is queued, running or cancelled, and a success once it has finished.
    /// </summary>
    public static readonly IReadOnlyList<(string Status, bool NeedsLastError, string Level)> JobLevels =
    [
        (ProcessingJobStatus.Pending, true, SystemLogLevels.Warning),
        (ProcessingJobStatus.Failed, false, SystemLogLevels.Error),
        (ProcessingJobStatus.HandlerOkFinalizeFailed, false, SystemLogLevels.Warning),
        (ProcessingJobStatus.Completed, false, SystemLogLevels.Success),
    ];

    /// <summary>A job's status as a person says it.</summary>
    public static readonly IReadOnlyDictionary<string, string> JobStatusLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ProcessingJobStatus.Pending] = "Queued",
        [ProcessingJobStatus.Leased] = "Running",
        [ProcessingJobStatus.Completed] = "Finished",
        [ProcessingJobStatus.Failed] = "Failed",
        [ProcessingJobStatus.Cancelled] = "Cancelled",
        [ProcessingJobStatus.HandlerOkFinalizeFailed] = "Recovery needed",
    };

    /// <summary>Server log lines by the part of Weir that wrote them, first match first, matched against the lower-cased logger name.</summary>
    private static readonly (string[] Fragments, string Category)[] ServerLoggerCategories =
    [
        (["backup"], SystemLogCategories.Backups),
        (["update"], SystemLogCategories.Updates),
        (["auth", "session", "rate_limit", "setup_code"], SystemLogCategories.SignIn),
        (["cleanup", "sweep"], SystemLogCategories.Cleanup),
        (["scan", "watched_folder", "watchedfolder"], SystemLogCategories.Scans),
        (["connection", "media_manager", "mediamanager", "download_client", "downloadclient", "handoff", "handback", "reconcil"], SystemLogCategories.Connections),
        (["library"], SystemLogCategories.Library),
        (["processing", "remux", "jobs", "worker"], SystemLogCategories.Processing),
    ];

    private const string KindNameDelimiter = ".";

    public static string EventCategory(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        foreach (var (prefix, category) in EventTypeCategories)
        {
            if (eventType.StartsWith(prefix, StringComparison.Ordinal))
            {
                return category;
            }
        }

        return SystemLogCategories.Weir;
    }

    public static string EventLevel(string? result) =>
        result is not null && EventResultLevels.TryGetValue(result, out var level) ? level : SystemLogLevels.Info;

    public static string JobCategory(string jobKind) =>
        JobKinds.FirstOrDefault(rule => rule.Kind == jobKind)?.Category ?? SystemLogCategories.Weir;

    /// <summary>A job kind in words; one without a rule reads as the second-to-last part of its name.</summary>
    public static string JobKindLabel(string jobKind)
    {
        ArgumentNullException.ThrowIfNull(jobKind);
        if (JobKinds.FirstOrDefault(rule => rule.Kind == jobKind) is { } known)
        {
            return known.Label;
        }

        var parts = jobKind.Split(KindNameDelimiter, StringSplitOptions.RemoveEmptyEntries);
        var name = (parts.Length >= 2 ? parts[^2] : jobKind).Replace('_', ' ');
        return name.Length == 0 ? jobKind : char.ToUpperInvariant(name[0]) + name[1..];
    }

    public static string JobStatusLabel(string status) => JobStatusLabels.GetValueOrDefault(status, status);

    public static string JobLevel(string status, string? lastError)
    {
        foreach (var (ruleStatus, needsLastError, level) in JobLevels)
        {
            if (ruleStatus == status && (!needsLastError || !string.IsNullOrEmpty(lastError)))
            {
                return level;
            }
        }

        return SystemLogLevels.Info;
    }

    /// <summary>A server log line's level: ERROR and CRITICAL are errors, WARNING a warning, anything else information.</summary>
    public static string ServerLevel(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" or "CRITICAL" => SystemLogLevels.Error,
        "WARNING" => SystemLogLevels.Warning,
        _ => SystemLogLevels.Info,
    };

    public static string ServerCategory(string logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var name = logger.ToLowerInvariant();
        foreach (var (fragments, category) in ServerLoggerCategories)
        {
            if (fragments.Any(fragment => name.Contains(fragment, StringComparison.Ordinal)))
            {
                return category;
            }
        }

        return SystemLogCategories.Weir;
    }
}
