using System.Globalization;

namespace Weir.Core.LibraryMode;

/// <summary>How a library-mode swap ended (#506).</summary>
public enum SwapOutcome
{
    /// <summary>The cleaned copy now has the original's name.</summary>
    Committed,

    /// <summary>Preflight: the file has more than one hard link (still seeding, #508). Nothing was done.</summary>
    Hardlinked,

    /// <summary>Preflight: the volume cannot hold a full copy plus the margin. Nothing was done.</summary>
    InsufficientSpace,

    /// <summary>Preflight: Weir cannot write to the folder, or the file is read-only. Nothing was done.</summary>
    NotWritable,

    /// <summary>The file is not there.</summary>
    SourceMissing,

    /// <summary>Another program holds the file (a Windows sharing violation, <c>EBUSY</c>). Not a failure: requeue with <see cref="SafeSwapRules.InUseRetryDelay"/>.</summary>
    InUse,

    /// <summary>The original changed while Weir worked. The cleaned copy was discarded and nothing was replaced.</summary>
    SourceChanged,

    /// <summary>The cleaned copy failed the output checks. It was discarded; the original is untouched.</summary>
    ValidationFailed,

    /// <summary>Something else went wrong before the commit. Everything was rolled back.</summary>
    Failed,
}

/// <summary>Which Weir leftover a file name is.</summary>
public enum SwapLeftoverKind
{
    /// <summary><c>&lt;name&gt;.weir-tmp&lt;ext&gt;</c>: a cleaned copy that was never put in place.</summary>
    Temp,

    /// <summary><c>&lt;name&gt;.weir-bak&lt;ext&gt;</c>: the original, moved aside during a commit.</summary>
    Backup,
}

/// <summary>
/// The pure rules of the crash-safe in-place swap (#506): the temp and backup names, the space margin,
/// the in-use backoff and the operator wording.
/// </summary>
public static class SafeSwapRules
{
    public const string TempMarker = ".weir-tmp";

    public const string BackupMarker = ".weir-bak";

    /// <summary>The free space kept beyond the file's own size when the workflow has not said otherwise: what a new workflow keeps free.</summary>
    public const long DefaultFreeSpaceMarginBytes = Processing.ProcessingLibraryRecord.DefaultMinimumFreeDiskSpaceMb * 1024 * 1024;

    /// <summary>What an in-use file waits before each retry (5, 15 and 60 minutes); after the last, it is reported as in use.</summary>
    public static readonly IReadOnlyList<TimeSpan> InUseBackoff =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
    ];

    public const string CommittedMessage = "Weir replaced the file with its cleaned copy.";

    public const string SourceChangedMessage = "The file changed while Weir was working; nothing was replaced";

    public const string HardlinkedMessage =
        "Skipped: this file is still shared with a download (seeding). Replacing it would break the link and double the disk space it uses.";

    public const string SourceMissingMessage = "The file is no longer there, so Weir had nothing to replace.";

    public const string ReadOnlyMessage = "This file is read-only, so Weir cannot replace it. Nothing was changed.";

    public const string InUseMessage =
        "Another program is using this file (for example, it is being played), so Weir left it alone and will try again later.";

    public const string InUseGaveUpMessage =
        "This file was still in use by another program after three tries, so Weir left it unchanged.";

    /// <summary>The temp output beside <paramref name="originalPath"/>: <c>&lt;name&gt;.weir-tmp&lt;ext&gt;</c>.</summary>
    public static string TempPath(string originalPath) => WithMarker(originalPath, TempMarker);

    /// <summary>Where the original waits during a commit: <c>&lt;name&gt;.weir-bak&lt;ext&gt;</c>.</summary>
    public static string BackupPath(string originalPath) => WithMarker(originalPath, BackupMarker);

    /// <summary>
    /// Whether <paramref name="path"/> is exactly a name <see cref="TempPath"/> or <see cref="BackupPath"/> produces,
    /// and for which original. Anything else, however similar, is not Weir's.
    /// </summary>
    public static bool TryParseLeftover(string path, out SwapLeftoverKind kind, out string originalPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = Path.GetFileName(path);
        var directory = path[..^name.Length];
        foreach (var (marker, candidate) in new[] { (TempMarker, SwapLeftoverKind.Temp), (BackupMarker, SwapLeftoverKind.Backup) })
        {
            var at = name.LastIndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var extension = name[(at + marker.Length)..];
            var originalName = name[..at] + extension;
            if (originalName.Length == 0 || !string.Equals(Path.GetFileName(WithMarker(originalName, marker)), name, StringComparison.Ordinal))
            {
                continue;
            }

            kind = candidate;
            originalPath = directory + originalName;
            return true;
        }

        kind = default;
        originalPath = string.Empty;
        return false;
    }

    /// <summary>The free bytes a swap of a <paramref name="sizeBytes"/> file needs on its volume: room for the copy, and the space the workflow keeps free.</summary>
    public static long RequiredFreeBytes(long sizeBytes, long keepFreeBytes) => Math.Max(0, sizeBytes) + Math.Max(0, keepFreeBytes);

    /// <summary>
    /// The wait before retrying a file found in use for the <paramref name="inUseCount"/>-th time (1-based):
    /// 5, 15, then 60 minutes. <see langword="null"/> once those are spent: report <see cref="InUseGaveUpMessage"/>.
    /// </summary>
    public static TimeSpan? InUseRetryDelay(int inUseCount) =>
        inUseCount >= 1 && inUseCount <= InUseBackoff.Count ? InUseBackoff[inUseCount - 1] : null;

    public static string InsufficientSpaceMessage(long requiredBytes, long availableBytes, long keepFreeBytes) =>
        $"Waiting: the drive this file is on has less than {FormatBytes(requiredBytes)} free ({FormatBytes(availableBytes)} free now), " +
        $"which is the file's size plus the {FormatBytes(keepFreeBytes)} this workflow keeps free. Nothing was changed. Weir tries again when there is room.";

    public static string NotWritableMessage(string detail) =>
        $"Weir cannot write to this file's folder, so it did not start. The system reported: {detail}";

    public static string ValidationFailedMessage(string? problem) =>
        "The cleaned copy did not pass Weir's output checks, so the original was kept" +
        (string.IsNullOrWhiteSpace(problem) ? "." : $": {problem}");

    public static string FailedMessage(string stage, string detail) =>
        $"Weir could not finish replacing the file while {stage}, so the original was kept. The system reported: {detail}";

    /// <summary>#735: the file was cleaned, but moving its original into the originals folder failed; the startup sweep retries it.</summary>
    public static string OriginalKeepFailedMessage(string destination, string detail) =>
        $"Weir replaced the file but could not move the original into {destination}; it will try again when it next starts. The system reported: {detail}";

    /// <summary>Bytes as the operator reads them: binary units, one decimal from KB up.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{(long)value} {units[0]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    private static string WithMarker(string originalPath, string marker)
    {
        ArgumentNullException.ThrowIfNull(originalPath);
        var name = Path.GetFileName(originalPath);
        if (name.Length == 0)
        {
            throw new ArgumentException("A file path is required.", nameof(originalPath));
        }

        var extension = Path.GetExtension(name);
        return string.Concat(originalPath.AsSpan(0, originalPath.Length - extension.Length), marker, extension);
    }
}
