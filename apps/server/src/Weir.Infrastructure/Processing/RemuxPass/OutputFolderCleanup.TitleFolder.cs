using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class OutputFolderCleanup
{
    private readonly record struct TitleFolder(string OutputRoot, string Folder, string MediaOutputFile);

    private static TitleFolder? LocateTitleFolder(WireObject output, ProcessingPathRuntime runtime, string watchedRoot, string source, string? finalOutputFile, string skipKey, string prefix, bool tv)
    {
        var label = tv ? "TV" : "Movies";
        var rootRaw = WireStrings.Strip(runtime.OutputFolder);
        if (rootRaw.Length == 0)
        {
            Skip(output, prefix, skipKey, tv
                ? "No TV output folder is configured, so TV output-folder cleanup was not evaluated."
                : "No Movies output folder is configured, so output-folder cleanup was not evaluated.");
            return null;
        }

        var outputRoot = RemuxPassPaths.Resolve(rootRaw);
        if (!Directory.Exists(outputRoot))
        {
            Skip(output, prefix, skipKey, tv
                ? "The TV output folder is missing on disk, so TV output-folder cleanup was skipped."
                : "The Movies output folder is missing on disk, so output-folder cleanup was skipped.");
            return null;
        }

        var watched = RemuxPassPaths.Resolve(watchedRoot);
        var src = RemuxPassPaths.Resolve(source);
        var relUnderWatched = RemuxPassPaths.RelativeTo(src, watched);
        if (relUnderWatched is null)
        {
            Skip(output, prefix, skipKey, tv
                ? "The source file is not under the saved TV watched folder, so TV output-folder cleanup was skipped."
                : $"The source file is not under the saved {label} watched folder, so output-folder cleanup was skipped.");
            return null;
        }

        var mediaOut = finalOutputFile is not null ? RemuxPassPaths.Resolve(finalOutputFile) : RemuxPassPaths.Resolve(Path.Join(outputRoot, relUnderWatched));
        if (!RemuxPassPaths.IsUnder(mediaOut, outputRoot))
        {
            Skip(output, prefix, skipKey, tv
                ? "The expected TV episode file path would sit outside the TV output folder, so TV output-folder cleanup was skipped."
                : "The expected movie file path would sit outside the Movies output folder, so output-folder cleanup was skipped.");
            return null;
        }

        var folder = Path.GetDirectoryName(mediaOut) ?? mediaOut;
        if (!RemuxPassPaths.IsUnder(folder, outputRoot))
        {
            Skip(output, prefix, skipKey, tv
                ? "The TV season output folder would sit outside the TV output root, so it was not changed."
                : "The movie output folder would sit outside the Movies output root, so it was not changed.");
            return null;
        }

        if (RemuxPassPaths.SamePath(folder, outputRoot))
        {
            Skip(output, prefix, skipKey, tv
                ? "The episode file sits directly in the TV output folder root, so a season folder is not removed here."
                : "The movie file sits directly in the Movies output folder root, so a per-title folder is not removed here.");
            return null;
        }

        return new TitleFolder(outputRoot, folder, mediaOut);
    }

    /// <summary>
    /// Why an output folder the manager has finished with still stays: it sits behind a link, or (for a movie) it also
    /// holds other films' outputs, as a collection pack's folder does. Null when it can go.
    /// </summary>
    private string? FolderKeptReason(string scope, TitleFolder place)
    {
        if (PathContainment.HasLinkBelowRoot(place.OutputRoot, place.Folder, _files))
        {
            return ReleaseFolderRemoval.LinkedFolderReason;
        }

        if (scope == ProcessingMediaScopes.Tv)
        {
            return null;
        }

        try
        {
            return ReleaseFolderRemoval.HoldsOtherVideos(place.Folder, place.MediaOutputFile, ReleaseFolderRemoval.VideoExtensions(null), _files)
                ? ReleaseFolderRemoval.OtherVideosFolderKeptReason
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ReleaseFolderRemoval.OtherVideosFolderKeptReason;
        }
    }
}
