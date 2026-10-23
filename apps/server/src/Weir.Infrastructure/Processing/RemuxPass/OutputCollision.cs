using System.Globalization;
using Weir.Core.Json;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>What to do, where to write, and the sentence explaining it.</summary>
public sealed record CollisionDecision(string Policy, string Action, string Destination, bool ReplacedExisting, string Reason)
{
    public bool Wrote => Action == "write";
}

/// <summary>
/// What to do when an output already exists at the path Processing is about to write.
/// <c>replace</c> stays the default; the other policies fall back to not replacing when a comparison cannot be made.
/// </summary>
public static class OutputCollision
{
    public const string Replace = "replace";
    public const string Skip = "skip";
    public const string KeepBoth = "keep_both";
    public const string ReplaceIfLarger = "replace_if_larger";
    public const string ReplaceIfNewer = "replace_if_newer";

    /// <summary>Every recognised collision policy.</summary>
    public static readonly IReadOnlyList<string> Policies = [Replace, Skip, KeepBoth, ReplaceIfLarger, ReplaceIfNewer];

    /// <summary>Anything unrecognised is the default policy, <c>replace</c>.</summary>
    public static string NormalizePolicy(string? raw)
    {
        var value = WireStrings.Strip(raw ?? string.Empty).ToLowerInvariant();
        return Policies.Contains(value, StringComparer.Ordinal) ? value : Replace;
    }

    /// <summary>
    /// The next free numbered name: <c>Film (2001).mkv</c> becomes <c>Film (2001) (2).mkv</c>, then <c>(3)</c>, up to <paramref name="limit"/>.
    /// Null when every one of those names is taken, so a name that is taken is never handed back as free.
    /// </summary>
    public static string? NextFreePath(string final, int limit = 999, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        var directory = Path.GetDirectoryName(final) ?? string.Empty;
        var (stem, suffix) = StemAndSuffix(Path.GetFileName(final));
        for (var attempt = 2; attempt <= limit; attempt++)
        {
            var candidate = Path.Join(directory, $"{stem} ({attempt.ToString(CultureInfo.InvariantCulture)}){suffix}");
            if (!Exists(candidate, files))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Decides, under the collision policy, whether and where the output is written when a file may already be at <paramref name="final"/>.</summary>
    public static CollisionDecision Decide(string final, string? source = null, string? staged = null, string? policy = null, IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(final);
        files ??= PhysicalFileSystem.Instance;
        var chosen = NormalizePolicy(policy);
        var name = Path.GetFileName(final);
        if (!Exists(final, files))
        {
            return new CollisionDecision(chosen, "write", final, false, "No file existed at the output path, so Weir wrote it there.");
        }

        switch (chosen)
        {
            case Skip:
                return new CollisionDecision(
                    chosen, "skip", final, false,
                    $"An output already exists at {name} and this workflow is set to leave existing outputs " +
                    "alone, so Weir kept the one that was already there.");
            case KeepBoth:
            {
                var destination = NextFreePath(final, files: files);
                if (destination is null)
                {
                    return new CollisionDecision(
                        chosen, "skip", final, false,
                        $"An output already exists at {name} and so does every numbered copy of it, so Weir kept the one that was already there " +
                        "and wrote nothing new.");
                }

                return new CollisionDecision(
                    chosen, "write", destination, false,
                    $"An output already exists at {name}, so Weir wrote this one alongside it as {Path.GetFileName(destination)}.");
            }

            case ReplaceIfLarger:
            {
                var existingSize = SizeOf(final, files);
                var newSize = staged is not null ? SizeOf(staged, files) : null;
                if (existingSize is null || newSize is null)
                {
                    return new CollisionDecision(
                        chosen, "skip", final, false,
                        $"Weir could not compare the new output with the existing {name}, so it kept the " +
                        "existing one rather than replacing a file it could not measure.");
                }

                var sizes = $"({newSize.Value.ToString(CultureInfo.InvariantCulture)} bytes against {existingSize.Value.ToString(CultureInfo.InvariantCulture)})";
                return newSize > existingSize
                    ? new CollisionDecision(chosen, "write", final, true, $"The new output is larger than the existing {name} {sizes}, so Weir replaced it.")
                    : new CollisionDecision(chosen, "skip", final, false, $"The new output is not larger than the existing {name} {sizes}, so Weir kept the existing one.");
            }

            case ReplaceIfNewer:
            {
                var existingTime = ModifiedOf(final, files);
                var sourceTime = source is not null ? ModifiedOf(source, files) : null;
                if (existingTime is null || sourceTime is null)
                {
                    return new CollisionDecision(
                        chosen, "skip", final, false,
                        $"Weir could not compare timestamps with the existing {name}, so it kept the " +
                        "existing one rather than replacing a file it could not date.");
                }

                return sourceTime > existingTime
                    ? new CollisionDecision(chosen, "write", final, true, $"The source is newer than the existing output at {name}, so Weir replaced it.")
                    : new CollisionDecision(chosen, "skip", final, false, $"The source is not newer than the existing output at {name}, so Weir kept the existing one.");
            }

            default:
                return new CollisionDecision(
                    Replace, "write", final, true,
                    $"An existing output at {name} was replaced, which is this workflow's collision policy.");
        }
    }

    /// <summary><c>Path.exists</c>: a file or a directory.</summary>
    private static bool Exists(string path, IFileSystem files) => files.FileExists(path) || files.DirectoryExists(path);

    private static (string Stem, string Suffix) StemAndSuffix(string name)
    {
        var index = name.LastIndexOf('.');
        return index <= 0 || index == name.Length - 1 ? (name, string.Empty) : (name[..index], name[index..]);
    }

    private static long? SizeOf(string path, IFileSystem files)
    {
        try
        {
            if (files.FileExists(path))
            {
                return files.GetLength(path);
            }

            // os.stat on a directory succeeds; its st_size is not meaningful but is a number.
            return files.DirectoryExists(path) ? 0 : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? ModifiedOf(string path, IFileSystem files)
    {
        try
        {
            if (files.FileExists(path))
            {
                return files.GetLastWriteTimeUtc(path);
            }

            return files.DirectoryExists(path) ? files.GetDirectoryLastWriteTimeUtc(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
