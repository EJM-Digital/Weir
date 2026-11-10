using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

/// <summary>Either a relative path Processing can use, or the reason it cannot be produced.</summary>
public sealed record HandoffPathResult(string? RelativeMediaPath, string? Problem)
{
    public bool Ok => RelativeMediaPath is not null;
}

/// <summary>
/// A manager's absolute file path made relative to a watched folder, textually and without touching
/// the filesystem, since the manager's host may not be this one.
/// </summary>
public static class HandoffPaths
{
    public static HandoffPathResult RelativeMediaPathForHandoff(string? watchedFolder, string? filePath)
    {
        var folder = WireStrings.Strip(watchedFolder ?? string.Empty);
        if (folder.Length == 0)
        {
            return new HandoffPathResult(
                null,
                "Weir's watched folder is not set for this media scope, so there is nowhere to resolve the hand-off against.");
        }

        var target = WireStrings.Strip(filePath ?? string.Empty);
        if (target.Length == 0)
        {
            return new HandoffPathResult(null, "The hand-off did not name a file path.");
        }

        var folderParts = Comparable(folder).Split('/').Where(p => p.Length > 0).ToList();
        var targetPartsCmp = Comparable(target).Split('/').Where(p => p.Length > 0).ToList();
        var targetPartsRaw = WireStrings.Strip(target.Replace('\\', '/')).Split('/').Where(p => p.Length > 0).ToList();

        // Stryker disable once Equality : a target with exactly the folder's parts leaves an empty relative path, which the check below refuses with the same message.
        if (targetPartsCmp.Count <= folderParts.Count || !targetPartsCmp.Take(folderParts.Count).SequenceEqual(folderParts, StringComparer.Ordinal))
        {
            return new HandoffPathResult(null, OutsideMessage(targetPartsRaw));
        }

        var relative = PosixJoin(targetPartsRaw.Skip(folderParts.Count));
        if (relative.Length == 0 || relative.Split('/').Contains(".."))
        {
            return new HandoffPathResult(null, OutsideMessage(targetPartsRaw));
        }

        return new HandoffPathResult(relative, null);
    }

    /// <summary>Joins separator-free parts with <c>/</c>; <c>.</c> parts drop out.</summary>
    private static string PosixJoin(IEnumerable<string> parts) => string.Join('/', parts.Where(p => p != "."));

    private static string Comparable(string part) =>
        WireStrings.Strip(part.Replace('\\', '/')).TrimEnd('/').ToLowerInvariant();

    /// <summary>
    /// Names only the file, never the full path either side gave: this can run before a caller is authenticated
    /// (an intake source with no secret configured yet), so it must not disclose the layout of Weir's own disk.
    /// </summary>
    private static string OutsideMessage(List<string> targetParts) =>
        $"The hand-off names {WireStrings.Repr(targetParts.Count > 0 ? targetParts[^1] : string.Empty)}, which is not inside Weir's watched folder for this media type. " +
        "Point the media manager and Weir at the same folder — both hosts have to see it at that path.";
}
