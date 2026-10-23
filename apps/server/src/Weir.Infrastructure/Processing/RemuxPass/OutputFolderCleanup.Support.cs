using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Rules;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class OutputFolderCleanup
{
    /// <summary>The media files directly inside <paramref name="folder"/>, in name order.</summary>
    public static IReadOnlyList<string> DirectChildMediaCandidates(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder)
                .Where(path => RemuxRules.MediaExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The newest file modification time under <paramref name="root"/>, in Unix seconds, or null when none can be read.</summary>
    public static double? NewestModifiedUnderTree(string root)
    {
        double? newest = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                var modified = Seconds(File.GetLastWriteTimeUtc(file));
                newest = newest is null ? modified : Math.Max(newest.Value, modified);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return newest;
    }

    private double Now() => (_time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;

    private static double Seconds(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    private static string? ResolveOrNull(string raw)
    {
        try
        {
            return RemuxPassPaths.Resolve(raw);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void InitFields(WireObject output, string prefix, string deleted, string path, string skip, string cascade)
    {
        SetDefault(output, deleted, WireBool.False);
        SetDefault(output, path, WireNull.Instance);
        SetDefault(output, skip, WireNull.Instance);
        SetDefault(output, $"{prefix}_truth_check", WireNull.Instance);
        SetDefault(output, $"{prefix}_truth_note", WireNull.Instance);
        SetDefault(output, $"{prefix}_age_seconds", WireNull.Instance);
        SetDefault(output, cascade, new WireArray());
        SetDefault(output, $"{prefix}_dry_run", WireNull.Instance);
    }

    private static void Skip(WireObject output, string prefix, string skipKey, string reason)
    {
        output.Set(skipKey, reason);
        output.Set($"{prefix}_truth_check", LibraryTruthVerdict.Skipped);
        output.Set($"{prefix}_truth_note", reason);
    }

    /// <summary><c>dict.setdefault</c>.</summary>
    public static void SetDefault(WireObject output, string key, WireValue value)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.ContainsKey(key))
        {
            output.Set(key, value);
        }
    }
}
