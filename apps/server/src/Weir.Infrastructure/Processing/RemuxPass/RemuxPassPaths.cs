using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>Resolved folders and output-side settings for one pass.</summary>
public sealed record ProcessingPathRuntime
{
    public required string WatchedFolder { get; init; }
    public required string OutputFolder { get; init; }
    public required string WorkFolderEffective { get; init; }
    public required bool WorkFolderIsDefault { get; init; }
    public string SidecarPatternsCsv { get; init; } = string.Empty;
    public bool PreserveOriginalTimestamps { get; init; }
    public string OutputCollisionPolicy { get; init; } = "replace";
    public string FfmpegStrictness { get; init; } = "normal";

    /// <summary>Which tool writes this library's output (<see cref="Weir.Core.Media.RemuxWriterChoice"/>).</summary>
    public string RemuxWriter { get; init; } = RemuxWriterChoice.Best;

    /// <summary>The library's <c>remove_original_after_success</c> as saved. What a pass does is <see cref="RemovesOriginals"/>, which a link to a media manager overrides.</summary>
    public bool RemoveOriginalAfterSuccess { get; init; } = true;

    /// <summary>The media managers the workflow is linked to when the pass starts, read from the links and not from a saved setting.</summary>
    public WorkflowManagerLinks ManagerLinks { get; init; } = WorkflowManagerLinks.None;

    /// <summary>Whether the pass may remove the source, its sidecars or its folder after cleaning: only a Weir-only workflow that asks for it.</summary>
    public bool RemovesOriginals => RemoveOriginalAfterSuccess && !ManagerLinks.KeepsOriginals;

    /// <summary>Why the source is still there when <see cref="RemovesOriginals"/> is false: the link, naming the manager, or the saved setting.</summary>
    public string KeptOriginalReason => ManagerLinks.KeepsOriginals ? ManagerLinks.KeptOriginalReason : RemuxPassRunner.KeptOriginalReason;

    /// <summary>The library's own media extensions, so removing a source never takes another video with it.</summary>
    public string MediaExtensionsCsv { get; init; } = string.Empty;
}

/// <summary>The rejected-file deletion's outcome.</summary>
public sealed record RejectedFileCleanupResult(bool Deleted, string Detail);

/// <summary>
/// Paths for a pass: the safe join under the watched folder, the library's folders, its rules and the rejected-file
/// cleanup.
/// </summary>
public static class RemuxPassPaths
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private static StringComparison PathComparison => Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The path absolute and normalised, with a leading <c>~</c> expanded to the user's home and no trailing separator.</summary>
    public static string Resolve(string raw)
    {
        var text = raw;
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
        }

        var full = Path.GetFullPath(text.Length == 0 ? "." : text);
        var root = Path.GetPathRoot(full)!;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it, compared component by component.</summary>
    public static bool IsUnder(string path, string root) => RelativeTo(path, root) is not null;

    /// <summary>
    /// The part of <paramref name="path"/> below <paramref name="root"/> with the platform separator (empty for the root itself), or null when
    /// <paramref name="path"/> is not under <paramref name="root"/>.
    /// </summary>
    public static string? RelativeTo(string path, string root)
    {
        var p = Split(path);
        var r = Split(root);
        if (p.Count < r.Count)
        {
            return null;
        }

        for (var i = 0; i < r.Count; i++)
        {
            if (!string.Equals(p[i], r[i], PathComparison))
            {
                return null;
            }
        }

        return string.Join(Path.DirectorySeparatorChar, p.Skip(r.Count));
    }

    /// <summary>Two resolved paths name the same place.</summary>
    public static bool SamePath(string a, string b) => RelativeTo(a, b) is { Length: 0 };

    /// <summary>A relative path with forward slashes.</summary>
    public static string Posix(string relative) => relative.Replace('\\', '/');

    private static List<string> Split(string path)
    {
        var full = Resolve(path);
        var root = Path.GetPathRoot(full)!;
        var parts = new List<string> { root.TrimEnd('\\', '/').Length == 0 ? root : root.TrimEnd('\\', '/') };
        parts.AddRange(full[root.Length..].Split('\\', '/').Where(part => part.Length > 0));
        return parts;
    }

    /// <summary>
    /// The full path of <paramref name="relativePath"/> under the watched folder, only when it stays under that folder.
    /// Throws <see cref="ArgumentException"/> otherwise, so a crafted relative path can never reach a file outside it.
    /// </summary>
    public static string ResolveMediaFileUnderRoot(string mediaRoot, string relativePath, IFileSystem? files = null)
    {
        var root = Resolve(mediaRoot);
        if (!(files ?? PhysicalFileSystem.Instance).DirectoryExists(root))
        {
            throw new ArgumentException("The watched folder (saved settings) must be an existing directory");
        }

        var rel = WireStrings.Strip(relativePath).Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0)
        {
            throw new ArgumentException("relative_media_path is required");
        }

        // Either character can make the path Weir resolves differ from the one the OS actually opens.
        if (rel.Contains('\0', StringComparison.Ordinal) || (Windows && rel.Contains(':', StringComparison.Ordinal)))
        {
            throw new ArgumentException("relative_media_path must not contain a NUL character or a colon");
        }

        if (rel.Split('/').Contains("..", StringComparer.Ordinal) || rel.StartsWith("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("relative_media_path must not contain parent segments");
        }

        var candidate = Resolve(Path.Join(root, rel));
        if (!IsUnder(candidate, root))
        {
            throw new ArgumentException("resolved file path escapes the saved watched folder");
        }

        return candidate;
    }

    /// <summary>The default work folder for a scope, under the Weir home folder.</summary>
    public static string DefaultWorkFolder(string weirHome, string mediaType) =>
        Path.Join(Resolve(weirHome), "processing", mediaType == "tv" ? "processing-tv-work" : "processing-movie-work");

    /// <summary>
    /// The library's own work folder, or the per-scope default when it has none. <c>IsDefault</c> says which,
    /// and callers use it to decide whether a folder that is not on disk is an error (a custom path) or
    /// something to create (the default).
    /// </summary>
    /// <remarks>
    /// There are no hard-coded Windows default paths here; see
    /// <see cref="Jobs.ProcessingLibraryFolders.EffectiveWorkFolder"/> for how a row still holding one is treated.
    /// </remarks>
    public static (string WorkFolder, bool IsDefault) EffectiveWorkFolder(ProcessingLibraryRecord library, string weirHome)
    {
        ArgumentNullException.ThrowIfNull(library);
        var raw = WireStrings.Strip(library.WorkFolder);
        if (raw.Length > 0)
        {
            return (raw, false);
        }

        return (DefaultWorkFolder(weirHome, WireStrings.Strip(library.MediaType).ToLowerInvariant()), true);
    }

    /// <summary>
    /// One library's folders, or the sentence saying why they cannot be used. <paramref name="links"/> are the media managers it is linked
    /// to right now (<see cref="LibraryStore.ManagerLinksAsync"/>), which decide whether the pass may remove an original.
    /// </summary>
    public static (ProcessingPathRuntime? Runtime, string? Problem) RuntimeForLibrary(ProcessingLibraryRecord library, string weirHome, WorkflowManagerLinks links, IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        files ??= PhysicalFileSystem.Instance;
        ArgumentNullException.ThrowIfNull(links);
        var label = WireStrings.Strip(library.Name).Length > 0 ? WireStrings.Strip(library.Name) : library.MediaType == "tv" ? "TV" : "Movies";
        var watchedRaw = WireStrings.Strip(library.WatchedFolder);
        if (watchedRaw.Length == 0)
        {
            return (null,
                $"The {label} workflow has no watched folder set. " +
                "Manual remux and folder-scan jobs need a watched folder to resolve relative paths. " +
                "Set it in Setup › Workflows before enqueueing or running those jobs.");
        }

        var watched = Resolve(watchedRaw);
        if (!files.DirectoryExists(watched))
        {
            return (null, $"The {label} workflow's watched folder must be an existing directory.");
        }

        var (workRaw, workIsDefault) = EffectiveWorkFolder(library, weirHome);
        var work = Resolve(workRaw);
        var outputRaw = WireStrings.Strip(library.OutputFolder);
        if (outputRaw.Length == 0)
        {
            return (null,
                $"The {label} workflow has no output folder set. " +
                "Set it in Setup › Workflows before running a live remux pass.");
        }

        var output = Resolve(outputRaw);
        if (!files.DirectoryExists(output))
        {
            return (null, $"The {label} workflow's output folder must be an existing directory.");
        }

        if (SameOrNested(work, output))
        {
            return (null, "The work/temp folder and output folder must be separate (no overlap or containment).");
        }

        if (SameOrNested(watched, output))
        {
            return (null, "The watched folder and output folder must be separate (no overlap or containment).");
        }

        if (SameOrNested(watched, work))
        {
            return (null, "The watched folder and work/temp folder must be separate (no overlap or containment).");
        }

        if (!workIsDefault && !files.DirectoryExists(work))
        {
            return (null, $"The {label} workflow's work/temp folder must be an existing directory when set to a custom path.");
        }

        return (new ProcessingPathRuntime
        {
            WatchedFolder = watched,
            OutputFolder = output,
            WorkFolderEffective = work,
            WorkFolderIsDefault = workIsDefault,
            SidecarPatternsCsv = library.SidecarPatternsCsv,
            PreserveOriginalTimestamps = library.PreserveOriginalTimestamps,
            OutputCollisionPolicy = string.IsNullOrEmpty(library.OutputCollisionPolicy) ? "replace" : library.OutputCollisionPolicy,
            FfmpegStrictness = string.IsNullOrEmpty(library.FfmpegStrictness) ? "normal" : library.FfmpegStrictness,
            RemuxWriter = RemuxWriterChoice.Normalize(library.RemuxWriter),
            RemoveOriginalAfterSuccess = library.RemoveOriginalAfterSuccess,
            ManagerLinks = links,
            MediaExtensionsCsv = library.MediaExtensionsCsv,
        }, null);
    }

    private static bool SameOrNested(string a, string b) => IsUnder(a, b) || IsUnder(b, a);

    /// <summary>
    /// A library's rule set, including the original-language and metadata options the plain
    /// conversion (<see cref="RuleSetConversion.ToRulesConfig"/>, the fallback for a library without one) leaves out.
    /// </summary>
    public static ProcessingRulesConfig RulesConfigFor(ProcessingRuleSetRecord ruleSet)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        return new ProcessingRulesConfig
        {
            PrimaryAudioLang = ruleSet.PrimaryAudioLang,
            SecondaryAudioLang = ruleSet.SecondaryAudioLang,
            TertiaryAudioLang = ruleSet.TertiaryAudioLang,
            DefaultAudioSlot = ruleSet.DefaultAudioSlot,
            RemoveCommentary = ruleSet.RemoveCommentary,
            // Normalized the same way as the fallback path (RuleSetConversion.ToRulesConfig) so both agree (#545).
            SubtitleMode = RuleSetConversion.NormalizeSubtitleMode(ruleSet.SubtitleMode),
            SubtitleLangs = [.. ruleSet.SubtitleLangsCsv.Split(',').Select(WireStrings.Strip).Where(x => x.Length > 0)],
            PreserveForcedSubs = ruleSet.PreserveForcedSubs,
            PreserveDefaultSubs = ruleSet.PreserveDefaultSubs,
            AudioPreferenceMode = RemuxRules.NormalizeAudioPreferenceMode(ruleSet.AudioPreferenceMode),
            AudioSortersJson = ruleSet.AudioSortersJson,
            SubtitleSortersJson = ruleSet.SubtitleSortersJson,
            RemoveHearingImpairedSubs = ruleSet.RemoveHearingImpairedSubs,
            AudioKeepMode = RemuxRules.NormalizeAudioKeepMode(ruleSet.AudioKeepMode),
            SubtitleMaxPerLanguage = ruleSet.SubtitleMaxPerLanguage,
            SubtitleQualityStrategy = RemuxRules.NormalizeSubtitleQualityStrategy(ruleSet.SubtitleQualityStrategy),
            OriginalLanguage = new OriginalLanguageRules
            {
                Enabled = ruleSet.KeepOriginalLanguage,
                AdditionalLanguages = OriginalLanguage.ParseAdditionalLanguages(ruleSet.OriginalLanguageAdditionalCsv),
                KeepOnlyFirst = ruleSet.OriginalLanguageKeepOnlyFirst,
                FirstIfNone = ruleSet.OriginalLanguageFirstIfNone,
                TreatEmptyAsOriginal = ruleSet.OriginalLanguageTreatEmptyAsOriginal,
            },
            Metadata = new MetadataRules
            {
                RemoveImages = ruleSet.RemoveImages,
                RemoveAttachments = ruleSet.RemoveAttachments,
                RemoveTitle = ruleSet.RemoveTitle,
                RemoveLanguageTags = ruleSet.RemoveLanguageTags,
                RemoveOtherMetadata = ruleSet.RemoveOtherMetadata,
                StandardizeTrackNames = ruleSet.StandardizeTrackNames,
                TrackNameTemplate = ruleSet.TrackNameTemplate,
                TrackNameOverrides = ruleSet.TrackNameOverrides,
                ClearVideoTrackNames = ruleSet.ClearVideoTrackNames,
                RemoveChapters = ruleSet.RemoveChapters,
            },
        };
    }

    /// <summary>Deletes one regular file under the watched folder, never a populated folder.</summary>
    public static RejectedFileCleanupResult CleanupRejectedFile(string watchedRoot, string filePath, string? action, IFileSystem? files = null)
    {
        files ??= PhysicalFileSystem.Instance;
        if (!string.Equals(WireStrings.Strip(action ?? "leave"), "delete_file", StringComparison.OrdinalIgnoreCase))
        {
            return new RejectedFileCleanupResult(false, "Weir left the rejected file in place because this workflow's cleanup action is Leave in place.");
        }

        string root;
        string source;
        try
        {
            root = Resolve(watchedRoot);
            source = Resolve(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new RejectedFileCleanupResult(false, $"Weir did not delete the rejected file because it was not safely inside the watched folder ({exception.Message}).");
        }

        if (!files.DirectoryExists(root) && !files.FileExists(root))
        {
            return new RejectedFileCleanupResult(false, $"Weir did not delete the rejected file because the watched folder {watchedRoot} could not be found.");
        }

        if (!files.DirectoryExists(source) && !files.FileExists(source))
        {
            return new RejectedFileCleanupResult(false, $"Weir did not delete the rejected file because {filePath} could not be found.");
        }

        if (!PathContainment.IsUnder(root, source))
        {
            return new RejectedFileCleanupResult(false, "Weir did not delete the rejected file because it was not safely inside the watched folder.");
        }

        if (!files.FileExists(source))
        {
            return new RejectedFileCleanupResult(false, "Weir did not delete the rejected path because it is not a regular file inside the watched folder.");
        }

        // A junction or symlinked folder above the file, or the file itself being a link, would delete something
        // that actually lives outside the watched folder (#786 review of #785).
        if (PathContainment.HasLinkBelowRoot(root, source, files))
        {
            return new RejectedFileCleanupResult(false, "Weir did not delete the rejected file because it is reached through a link, not a plain path inside the watched folder.");
        }

        try
        {
            files.DeleteFile(source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RejectedFileCleanupResult(false, $"Weir could not delete the rejected file because it is locked or unavailable ({exception.Message}).");
        }

        // Empty folders left behind are removed up to, never including, the watched folder. Only a filesystem root has no parent
        // folder, and nothing is under a root, so a folder the loop sees always has one.
        var parent = Path.GetDirectoryName(source)!;
        while (PathContainment.IsUnder(root, parent))
        {
            try
            {
                files.DeleteDirectory(parent, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                break;
            }

            parent = Path.GetDirectoryName(parent)!;
        }

        return new RejectedFileCleanupResult(true, "Weir deleted the rejected file because this workflow's cleanup action is Delete rejected file.");
    }
}
