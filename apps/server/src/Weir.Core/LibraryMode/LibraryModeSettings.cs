using Weir.Core.Json;
using Weir.Core.Processing;

namespace Weir.Core.LibraryMode;

/// <summary>
/// Library mode's job kinds (#505) and where they sort against the download pipeline's jobs.
/// </summary>
/// <remarks>
/// Per-library settings (<see cref="LibrarySettings"/>) live in <c>libraries</c> columns and the
/// <c>library_folders</c> table, and the scan index in <c>library_files</c> (#557) — see
/// <c>Weir.Infrastructure.LibraryMode.LibrarySettingsStore</c>/<c>LibraryScanStore</c> and
/// <c>docs/archive/server-port-notes.md</c>, "Library mode". <see cref="ScanKind"/> is an ordinary <c>jobs</c> row (a scan
/// is real, visible work with a lifecycle); its bulky per-file payload lives in <c>library_files</c>.
/// </remarks>
public static class LibraryModeJobKinds
{
    /// <summary>
    /// One row per requested scan, an ordinary job. Its completed payload keeps only a small
    /// <c>ok</c>/<c>generated_at</c>/<c>errors</c> outcome; the file list itself lives in
    /// <c>library_files</c> (see <c>LibraryScanStore</c>). The latest completed row for a library is read
    /// as that library's current scan status.
    /// </summary>
    public const string ScanKind = "processing.library.scan.v1";

    /// <summary>One row per file Weir is asked to clean in place. Runs the remux pass in <c>mode: library</c>, then the #506 safe swap.</summary>
    public const string CleanKind = "processing.library.clean.v1";

    public static string ScanDedupeKey(long libraryId) => $"{ScanKind}:{libraryId}:{Guid.NewGuid():N}";

    /// <summary>The dedupe-key prefix every scan row for a library shares, for "find the latest one".</summary>
    public static string ScanDedupeKeyPrefix(long libraryId) => $"{ScanKind}:{libraryId}:";

    public static string CleanDedupeKey(long libraryId, string path)
    {
        // Bounded and filesystem-agnostic: jobs.dedupe_key is VARCHAR(512), and a path can contain
        // anything. The hash keeps one row per (library, path) without ever running long or needing escaping.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)));
        return $"{CleanKind}:{libraryId}:{hash}";
    }
}

/// <summary>
/// Library jobs (scan and clean) always sort after every download-pipeline job: #505 says "library jobs queue behind download
/// jobs". <c>jobs.priority</c> is claimed highest-first (<c>ORDER BY priority DESC, id ASC</c>), and a stuck job's
/// priority is only ever bumped upward to <c>max(pending) + 1</c>, so a library job at this very low, fixed priority is always
/// claimed after any real download-pipeline job, however long those have waited.
/// </summary>
public static class LibraryModePriority
{
    public const int Low = -1_000_000;
}

/// <summary>
/// One library's #505 settings: where it looks, whether the off-by-default schedule runs, #508's two
/// preflight settings — whether cleaning touches a file still shared with a download (default false, since
/// cleaning one doubles disk use instead of reducing it) and whether a clean that would make a manager
/// re-download the title is skipped rather than performed (default true, the safer default) — and #735's
/// "keep the original after clean" (default false, so an upgrade changes nothing).
/// </summary>
/// <param name="Folders">The #505 library folders Weir scans and cleans in place.</param>
/// <param name="ScheduleEnabled">The off-by-default "Scheduled scan and clean".</param>
/// <param name="CleanHardlinkedFiles">#508 step 1: clean a file even while another name still shares its data (seeding).</param>
/// <param name="SkipIfManagerWouldRedownload">#508 step 2: skip a clean that would make a manager re-download the title.</param>
/// <param name="KeepOriginalAfterClean">
/// Off by default: a clean's pre-clean original is deleted once the cleaned copy is safely in place, as it
/// always has been. On: the original is moved into <see cref="OriginalsFolder"/> instead, so a removed track
/// can be recovered from it. See <see cref="Weir.Core.LibraryMode.OriginalsPathPlanner"/>.
/// </param>
/// <param name="OriginalsFolder">
/// Where a kept original goes; blank (the default) means the <see cref="Weir.Core.LibraryMode.OriginalsPathPlanner.DefaultFolderName"/>
/// folder inside whichever library folder held the file. Ignored while <see cref="KeepOriginalAfterClean"/> is off.
/// </param>
/// <param name="RuleSetId">
/// The rules profile that cleans this library's existing files; null (the default) means the same profile as the
/// workflow. See <c>Weir.Infrastructure.LibraryMode.LibraryModeRules</c>.
/// </param>
public sealed record LibrarySettings(
    IReadOnlyList<string> Folders,
    bool ScheduleEnabled,
    bool CleanHardlinkedFiles = false,
    bool SkipIfManagerWouldRedownload = true,
    bool KeepOriginalAfterClean = false,
    string OriginalsFolder = "",
    long? RuleSetId = null)
{
    public static LibrarySettings Empty { get; } = new([], false);

    public WireObject ToPayload(long libraryId) => new WireObject()
        .Set("library_id", libraryId)
        .Set("library_folders", new WireArray(Folders.Select(f => (WireValue)new WireString(f))))
        .Set("library_schedule_enabled", ScheduleEnabled)
        .Set("clean_hardlinked_files", CleanHardlinkedFiles)
        .Set("skip_if_manager_would_redownload", SkipIfManagerWouldRedownload)
        .Set("keep_original_after_clean", KeepOriginalAfterClean)
        .Set("originals_folder", OriginalsFolder)
        .Set("library_rule_set_id", RuleSetId);

    public static LibrarySettings FromPayload(WireObject? payload)
    {
        if (payload is null)
        {
            return Empty;
        }

        var folders = payload.Get("library_folders") is WireArray list
            ? list.Items.OfType<WireString>().Select(s => s.Value).Where(s => s.Length > 0).ToList()
            : [];
        var scheduleEnabled = payload.Get("library_schedule_enabled") is WireBool { Value: true };
        // Absent on a settings row written before #508 (or a brand-new library): the documented defaults.
        var cleanHardlinkedFiles = payload.Get("clean_hardlinked_files") is WireBool { Value: true };
        var skipIfManagerWouldRedownload = payload.Get("skip_if_manager_would_redownload") is not WireBool { Value: false };
        // Absent on a settings row written before #735: off, and the default originals folder.
        var keepOriginalAfterClean = payload.Get("keep_original_after_clean") is WireBool { Value: true };
        var originalsFolder = payload.Get("originals_folder") is WireString originalsFolderValue ? originalsFolderValue.Value : "";
        var ruleSetId = payload.Get("library_rule_set_id") is WireInteger ruleSetValue ? (long?)ruleSetValue.Value : null;
        return new LibrarySettings(
            folders, scheduleEnabled, cleanHardlinkedFiles, skipIfManagerWouldRedownload, keepOriginalAfterClean, originalsFolder, ruleSetId);
    }
}

/// <summary>A library folder failed validation (mirrors <see cref="ProcessingLibraryException"/> for the same family of checks).</summary>
public sealed class LibraryModeException : Exception
{
    public LibraryModeException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Library folders (#505 point 1): one or more folders Weir cleans in place, separate from a library's watched/work/output
/// folders and validated the same way (<see cref="LibraryRules.ValidateFolders"/>'s overlap check, reused rather than
/// duplicated).
/// </summary>
public static class LibraryFolderRules
{
    /// <summary>
    /// Normalizes, de-duplicates and validates that none of <paramref name="folders"/> overlaps the library's own
    /// watched/work/output folders or each other. Overlap with another library's folders is not checked here: library folders
    /// may deliberately be the same folders a media manager (or another Weir library) already watches — #505 says so
    /// explicitly ("These may be the same folders a manager uses, or not"). Each folder also goes through
    /// <see cref="LibraryRules.ValidateFolderPath"/>: absolute, no <c>..</c> segment, not a reserved system folder.
    /// </summary>
    /// <param name="folders">The library folders an operator submitted.</param>
    /// <param name="library">The library they belong to, for the watched/work/output overlap check.</param>
    /// <param name="weirHome">Weir's own data folder, passed through to <see cref="LibraryRules.ValidateFolderPath"/>.</param>
    public static IReadOnlyList<string> Validate(IReadOnlyList<string> folders, ProcessingLibraryRecord library, string? weirHome = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(library);
        var trimmed = folders.Select(f => (f ?? string.Empty).Trim()).Where(f => f.Length > 0).ToList();
        var normalizedSeen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var folder in trimmed)
        {
            ValidateOne("library", folder, library, weirHome);
            var normalized = LibraryRules.NormalizeFolder(folder)!;
            if (!normalizedSeen.Add(normalized))
            {
                continue;
            }

            result.Add(folder);
        }

        return result;
    }

    /// <summary>
    /// #735's per-library originals folder, validated exactly like a library folder above (same path rules, same
    /// watched/work/output overlap check), plus two checks specific to sitting near a scan folder. Blank means "use the
    /// default <see cref="OriginalsPathPlanner.DefaultFolderName"/> folder inside whichever library folder holds the file",
    /// so an empty or whitespace-only value is normalized to blank rather than rejected.
    /// </summary>
    /// <param name="raw">The originals folder an operator submitted, or blank for the default.</param>
    /// <param name="scanFolders">
    /// This library's already-validated #505 scan folders (<see cref="Validate"/>'s return value): sitting inside one of
    /// them is the supported, default-shaped case, but being equal to or an ancestor of one would make
    /// <c>Weir.Infrastructure.LibraryMode.LibraryFileWalker</c>'s exclusion swallow the whole scan folder, so that is
    /// refused. A custom folder inside a scan folder must be dot-prefixed, so a media manager watching that folder
    /// does not import the originals it holds.
    /// </param>
    /// <param name="library">The library it belongs to, for the watched/work/output overlap check.</param>
    /// <param name="weirHome">Weir's own data folder, passed through to <see cref="LibraryRules.ValidateFolderPath"/>.</param>
    public static string ValidateOriginalsFolder(string? raw, IReadOnlyList<string> scanFolders, ProcessingLibraryRecord library, string? weirHome = null)
    {
        ArgumentNullException.ThrowIfNull(scanFolders);
        ArgumentNullException.ThrowIfNull(library);
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        ValidateOne("originals", trimmed, library, weirHome);

        var normalized = LibraryRules.NormalizeFolder(trimmed)!;
        var dotPrefixed = LastSegment(trimmed).StartsWith('.');
        foreach (var scanFolder in scanFolders)
        {
            var scanNormalized = LibraryRules.NormalizeFolder(scanFolder);
            if (scanNormalized is null)
            {
                continue;
            }

            if (string.Equals(normalized, scanNormalized, StringComparison.Ordinal) || LibraryRules.IsAncestor(normalized, scanNormalized))
            {
                throw new LibraryModeException(
                    $"The originals folder can't be the same as, or contain, library folder '{scanFolder}'. It can sit inside a library folder, just not around one.");
            }

            if (!dotPrefixed && LibraryRules.IsAncestor(scanNormalized, normalized))
            {
                throw new LibraryModeException(
                    "Inside a library folder, the originals folder's name must start with a dot (e.g. .weir-originals) so media managers don't import the originals.");
            }
        }

        return trimmed;
    }

    /// <summary>The last path segment of <paramref name="path"/>, under either separator style.</summary>
    private static string LastSegment(string path)
    {
        var trimmedPath = path.TrimEnd('\\', '/');
        var lastSeparator = trimmedPath.LastIndexOfAny(['\\', '/']);
        return lastSeparator < 0 ? trimmedPath : trimmedPath[(lastSeparator + 1)..];
    }

    /// <summary>The checks a single library-folder-shaped path must pass: Weir's general folder rules, plus no overlap
    /// with this library's own watched/work/output folders.</summary>
    private static void ValidateOne(string label, string folder, ProcessingLibraryRecord library, string? weirHome)
    {
        try
        {
            LibraryRules.ValidateFolderPath(label, folder, weirHome);
        }
        catch (ProcessingLibraryException exception)
        {
            throw new LibraryModeException(exception.Message);
        }

        var normalized = LibraryRules.NormalizeFolder(folder)!;
        var subject = label == "originals" ? "The originals folder" : "Library folder";
        foreach (var (otherLabel, other) in new[] { ("watched", library.WatchedFolder), ("work", library.WorkFolder), ("output", library.OutputFolder) })
        {
            var otherNormalized = LibraryRules.NormalizeFolder(other);
            if (otherNormalized is not null && LibraryRules.FoldersOverlap(normalized, otherNormalized))
            {
                throw new LibraryModeException(
                    $"{subject} '{folder}' overlaps this workflow's {otherLabel} folder. Library folders must be separate from the folders the download pipeline uses.");
            }
        }
    }
}
