using Weir.Core.Json;
using Weir.Core.Text;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>One sidecar that travelled.</summary>
public sealed record MigratedSidecar(string Source, string Destination);

/// <summary>What travelled, what did not, and whether deletion may proceed.</summary>
public sealed class SidecarMigrationResult
{
    public List<MigratedSidecar> Migrated { get; } = [];

    /// <summary>Sidecars that exist and could not be copied. Non-empty means the source folder must not be deleted.</summary>
    public List<string> Failures { get; } = [];

    public List<string> Skipped { get; } = [];

    public bool BlocksSourceDeletion => Failures.Count > 0;

    public string BlockingReason => Failures.Count == 0
        ? string.Empty
        : "Weir did not remove the source folder because it could not copy " +
          $"{Plural.Of(Failures.Count, "file")} that {Plural.Noun(Failures.Count, "was", "were")} set to travel with the video: " +
          $"{string.Join("; ", Failures)}. The source is left in place so nothing is lost.";
}

/// <summary>
/// Carry sidecar files to the output before the source folder is deleted. A sidecar that is not there is not a failure; one that exists and could not be copied blocks the deletion.
/// </summary>
public static class SidecarMigration
{
    /// <summary>The sidecar patterns used when a library sets none.</summary>
    public static readonly IReadOnlyList<string> DefaultPatterns = [".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".nfo", ".jpg", ".png"];

    /// <summary>Parses a pattern list: lower-cased, dotted, de-duplicated. Empty means migrate nothing.</summary>
    public static IReadOnlyList<string> ParsePatterns(string? csv)
    {
        var output = new List<string>();
        foreach (var raw in (csv ?? string.Empty).Split(','))
        {
            var text = WireStrings.Strip(raw).ToLowerInvariant();
            if (text.Length == 0)
            {
                continue;
            }

            if (!text.StartsWith('.'))
            {
                text = "." + text;
            }

            if (!output.Contains(text, StringComparer.Ordinal))
            {
                output.Add(text);
            }
        }

        return output;
    }

    /// <summary>
    /// Files beside the video whose name starts with its stem and ends with a pattern, in case-insensitive
    /// name order. Matching the stem keeps one film's subtitles from being handed to another in the same folder.
    /// </summary>
    public static IReadOnlyList<string> FindSidecars(string sourceMedia, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count == 0)
        {
            return [];
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(sourceMedia))!;
        var stem = Stem(Path.GetFileName(sourceMedia)).ToLowerInvariant();
        List<string> entries;
        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(folder).OrderBy(p => Path.GetFileName(p).ToLowerInvariant(), StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var source = Path.GetFullPath(sourceMedia);
        var found = new List<string>();
        foreach (var entry in entries)
        {
            if (!File.Exists(entry) || string.Equals(Path.GetFullPath(entry), source, StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(entry).ToLowerInvariant();
            if (!name.StartsWith(stem, StringComparison.Ordinal) || !patterns.Any(pattern => name.EndsWith(pattern, StringComparison.Ordinal)))
            {
                continue;
            }

            found.Add(entry);
        }

        return found;
    }

    /// <summary>Where a sidecar goes: renamed to the output video's stem, keeping the trailing part (<c>.en.srt</c>).</summary>
    public static string DestinationFor(string sidecar, string sourceMedia, string outputMedia)
    {
        var sidecarName = Path.GetFileName(sidecar);
        var sourceStem = Stem(Path.GetFileName(sourceMedia));
        var trailing = sidecarName.Length >= sourceStem.Length ? sidecarName[sourceStem.Length..] : string.Empty;
        return Path.Join(Path.GetDirectoryName(outputMedia), Stem(Path.GetFileName(outputMedia)) + trailing);
    }

    /// <summary>Copies the sidecars to the output, never moves them, so a refused deletion loses nothing.</summary>
    public static async Task<SidecarMigrationResult> MigrateAsync(string sourceMedia, string outputMedia, IReadOnlyList<string> patterns, bool preserveTimestamps = false)
    {
        var result = new SidecarMigrationResult();
        if (patterns.Count == 0)
        {
            return result;
        }

        foreach (var sidecar in FindSidecars(sourceMedia, patterns))
        {
            var destination = DestinationFor(sidecar, sourceMedia, outputMedia);
            var destinationName = Path.GetFileName(destination);
            if (File.Exists(destination))
            {
                // Already there from an earlier pass; overwriting could replace an edited subtitle.
                result.Skipped.Add($"{destinationName} was already in the output folder, so it was not replaced.");
                continue;
            }

            if (Directory.Exists(destination))
            {
                result.Failures.Add($"{Path.GetFileName(sidecar)} (something else already exists at {destination})");
                continue;
            }

            try
            {
                await FileLifecycle.SafeCopyToFinalAsync(sidecar, destination, preserveMetadata: preserveTimestamps).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileLifecycleException or IOException or UnauthorizedAccessException)
            {
                result.Failures.Add($"{Path.GetFileName(sidecar)} ({exception.Message})");
                continue;
            }

            result.Migrated.Add(new MigratedSidecar(sidecar, destination));
        }

        return result;
    }

    /// <summary>The output gets the source's times. Returns a problem, or null. Never fatal.</summary>
    public static string? ApplyOriginalTimestamps(string sourceMedia, string outputMedia, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        DateTime accessed;
        DateTime modified;
        try
        {
            if (!files.FileExists(sourceMedia))
            {
                throw new FileNotFoundException($"{sourceMedia} could not be found");
            }

            accessed = files.GetLastAccessTimeUtc(sourceMedia);
            modified = files.GetLastWriteTimeUtc(sourceMedia);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Weir could not read the original file's timestamps ({exception.Message}).";
        }

        try
        {
            if (!files.FileExists(outputMedia))
            {
                throw new FileNotFoundException($"{outputMedia} could not be found");
            }

            files.SetLastAccessTimeUtc(outputMedia, accessed);
            files.SetLastWriteTimeUtc(outputMedia, modified);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Weir could not apply the original timestamps to the output ({exception.Message}).";
        }

        return null;
    }

    private static string Stem(string name)
    {
        var index = name.LastIndexOf('.');
        return index <= 0 || index == name.Length - 1 ? name : name[..index];
    }
}
