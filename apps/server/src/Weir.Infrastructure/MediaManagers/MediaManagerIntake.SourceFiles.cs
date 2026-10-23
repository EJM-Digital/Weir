using Weir.Core.MediaManagers;
using Weir.Infrastructure.IO;

namespace Weir.Infrastructure.MediaManagers;

public sealed partial class MediaManagerIntake
{
    /// <summary>
    /// The files a hand-off lists in its <c>sourceFiles</c>, as paths in the library. Only these are the hand-off's: the rest of the
    /// folder is not read, recorded or reported. A file that lies outside the folder the hand-off names, or that is not there, refuses
    /// the whole hand-off rather than falling back to the folder. The listed files then go through the choice a folder hand-off makes
    /// among its videos (<see cref="ChosenVideos"/>), so a listed sample or non-video is left out exactly as it would be in the folder.
    /// </summary>
    public static List<string> HandoffListedFiles(IntakeLibrary library, string relativePath, MediaManagerImportEvent importEvent, IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        files ??= PhysicalFileSystem.Instance;
        ArgumentNullException.ThrowIfNull(importEvent);
        var resolved = HandoffSourceFiles.Resolve(importEvent.FilePath, relativePath, importEvent.SourceFiles ?? [], OperatingSystem.IsWindows());
        if (!resolved.Ok)
        {
            throw new IntakeRefusedException(400, resolved.Problem!);
        }

        var listed = resolved.RelativePaths!;
        foreach (var target in listed)
        {
            if (!files.FileExists(Path.Join(library.WatchedFolder, target)))
            {
                throw new IntakeRefusedException(400, HandoffSourceFiles.MissingDetail(target));
            }
        }

        // A hand-off that names one file and lists it is that file, as it is without a list.
        if (listed.Contains(relativePath))
        {
            return [relativePath];
        }

        var folderDepth = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        List<IReadOnlyList<string>> videos =
        [
            .. listed
                .Where(target => IntakeRules.IsMediaCandidateName(Path.GetFileName(target)))
                .Select(target => (IReadOnlyList<string>)[.. target.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(folderDepth)]),
        ];
        return ChosenVideos(relativePath, videos, HandoffSourceFiles.NoVideoDetail);
    }
}
