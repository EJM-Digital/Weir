using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

public static partial class ManagerSetupRules
{
    /// <summary>
    /// <see cref="EvaluateDeluno(string, string, string, string, IReadOnlyList{ManagerLibraryDescriptor}, IReadOnlyList{ManagerDownloadClientDescriptor}?, IFolderProbe?)"/>
    /// once Deluno has been asked where the chosen library's downloads land (<paramref name="answer"/>). When it answered,
    /// every folder it named is turned into Weir's view through Deluno's path mappings and judged: inside the watched folder
    /// (each client's save folder and the library's downloads folder) or the output folder (the processed-output folder) is
    /// <see cref="SetupCheckLine.Ok"/>, outside is <see cref="SetupCheckLine.Problem"/>, and a client Deluno could not
    /// ask stays <see cref="SetupCheckLine.Unverified"/>. When it did not answer, the manifest's lines stand and say why. When it
    /// says it no longer has the library, that one line stands alone, naming <paramref name="workflowName"/> when it is known.
    /// </summary>
    public static DelunoSetupResult EvaluateDelunoWithDestinations(
        string managerLabel,
        string mediaScope,
        string watchedFolder,
        string outputFolder,
        DelunoLibraryChoice choice,
        IReadOnlyList<ManagerDownloadClientDescriptor>? downloadClients,
        DelunoDestinationsAnswer answer,
        IFolderProbe? probe = null,
        string? workflowName = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(answer);
        if (choice.Library is not { } chosen)
        {
            return new DelunoSetupResult(null, null, choice.Lines);
        }

        if (answer.Status == DelunoDestinationsStatus.LibraryNotFound)
        {
            var removed = new SetupCheckLine(SetupCheckLine.Unverified, DelunoDestinationNotices.LibraryRemoved(managerLabel, chosen.Name, workflowName));
            return new DelunoSetupResult(null, null, [removed]);
        }

        if (answer.Status != DelunoDestinationsStatus.Read)
        {
            var manifest = EvaluateDelunoManifest(managerLabel, mediaScope, watchedFolder, outputFolder, choice, downloadClients, probe);
            return WithNoticeAboutDestinations(managerLabel, manifest, answer);
        }

        var library = answer.Libraries.FirstOrDefault(candidate => string.Equals(candidate.LibraryId, chosen.Key, StringComparison.OrdinalIgnoreCase));
        return library is null
            ? new DelunoSetupResult(null, null, [new SetupCheckLine(SetupCheckLine.Unverified, DelunoDestinationNotices.LibraryGone(managerLabel))])
            : EvaluateDelunoDestinations(managerLabel, watchedFolder, outputFolder, choice, library, probe);
    }

    private static DelunoSetupResult WithNoticeAboutDestinations(string managerLabel, DelunoSetupResult manifest, DelunoDestinationsAnswer answer)
    {
        switch (answer.Status)
        {
            case DelunoDestinationsStatus.NotOffered:
                return manifest with { Lines = [.. manifest.Lines, new SetupCheckLine(SetupCheckLine.Note, DelunoDestinationNotices.RouteMissing())] };
            case DelunoDestinationsStatus.NeedsImportsScope:
                var fix = new SetupCheckLine(SetupCheckLine.Unverified, DelunoDestinationNotices.NeedsImportsScope(managerLabel));
                return manifest with { Lines = [fix, .. manifest.Lines] };
            default:
                var unreachable = new SetupCheckLine(SetupCheckLine.Unverified, answer.Detail ?? $"{managerLabel} did not say where its downloads are saved.");
                return manifest with { Lines = [.. manifest.Lines, unreachable] };
        }
    }

    private static DelunoSetupResult EvaluateDelunoDestinations(
        string managerLabel, string watchedFolder, string outputFolder, DelunoLibraryChoice choice, DelunoLibraryDestinations library, IFolderProbe? probe)
    {
        var watched = WireStrings.Strip(watchedFolder);
        var output = WireStrings.Strip(outputFolder);
        var downloads = MapFolder(library, library.DownloadsPath);
        var processed = MapFolder(library, library.ProcessorOutputPath);

        var lines = new List<SetupCheckLine>(choice.Lines);
        // Deluno's default is no downloads folder of its own: the clients' folders say it, and their lines speak for it.
        if (downloads is not null || library.Destinations.All(destination => destination.SaveFolder is null))
        {
            lines.Add(DownloadsVerdict(managerLabel, library, downloads, watched, probe));
        }

        lines.AddRange(library.Destinations
            .Select(destination => DestinationVerdict(managerLabel, library, destination, watched, probe))
            .OfType<SetupCheckLine>());
        if (processed is not null)
        {
            lines.Add(OutputVerdict(managerLabel, library, processed, output, probe));
        }

        var suggested = downloads?.Path ?? WorkflowSyncFolders.Watched(managerLabel, library.LibraryName, null, library).Folder;
        return new DelunoSetupResult(suggested, processed?.Path, lines);
    }

    private static MappedPath? MapFolder(DelunoLibraryDestinations library, string? folder) =>
        folder is null ? null : DelunoPathMappings.Apply(library.PathMappings, folder);

    /// <summary>A folder as Deluno wrote it, with Weir's view of it when the two differ.</summary>
    private static string Seen(MappedPath folder) => folder.WasMapped ? $"{folder.Original} (Weir sees it as {folder.Path})" : folder.Path;

    private static SetupCheckLine DownloadsVerdict(string managerLabel, DelunoLibraryDestinations library, MappedPath? downloads, string watched, IFolderProbe? probe)
    {
        if (downloads is null)
        {
            return new SetupCheckLine(
                SetupCheckLine.Unverified,
                $"{managerLabel} does not say where {library.LibraryName}'s downloads finish, so Weir cannot verify that its hand-offs sit inside this workflow's watched folder. " +
                $"Set the downloads folder in {managerLabel} (or the clients' category folders) and Weir will pick it up.");
        }

        if (watched.Length == 0)
        {
            return new SetupCheckLine(
                SetupCheckLine.Problem,
                $"{managerLabel} reports {library.LibraryName}'s downloads in {Seen(downloads)}, but this workflow has no watched folder. Set the watched folder to {downloads.Path}.");
        }

        if (Inside(downloads.Path, watched) || InsideOnceLinksAreFollowed(downloads.Path, watched, probe))
        {
            return new SetupCheckLine(
                SetupCheckLine.Ok,
                $"{managerLabel}'s {library.LibraryName} library finishes downloads in {Seen(downloads)}, inside this workflow's watched folder.");
        }

        var fix = downloads.WasMapped
            ? DelunoPathMappingAdvice.WhenMappedElsewhere(managerLabel, downloads, "the watched folder")
            : DelunoPathMappingAdvice.WhenNoMappingCovers(managerLabel, library.PathMappings, downloads.Original, watched, "the watched folder");
        return new SetupCheckLine(
            SetupCheckLine.Problem,
            $"{managerLabel}'s {library.LibraryName} library finishes downloads in {Seen(downloads)}, which isn't inside this workflow's watched folder {watched}. {fix}");
    }

    /// <summary>One download client's line, or null when there is nothing to compare because the workflow has no watched folder.</summary>
    private static SetupCheckLine? DestinationVerdict(
        string managerLabel, DelunoLibraryDestinations library, DelunoDestination destination, string watched, IFolderProbe? probe)
    {
        var target = destination.Category.Length == 0 ? "its downloads" : $"the \"{destination.Category}\" {destination.CategoryKind}";
        switch (destination.Status)
        {
            case DelunoDestination.Problem:
                var reason = destination.Message.Length == 0 ? "." : $": {destination.Message}";
                return new SetupCheckLine(SetupCheckLine.Problem, $"{managerLabel} reports a problem with {destination.ClientName} for {target}{reason}");
            case DelunoDestination.Ok when destination.SaveFolder is not null:
                return watched.Length == 0 ? null : SaveFolderVerdict(managerLabel, library, destination, target, watched, probe);
            case DelunoDestination.Ok:
                return new SetupCheckLine(
                    SetupCheckLine.Unverified,
                    $"{managerLabel} didn't say where {destination.ClientName} saves {target}, so Weir can't verify those downloads land inside the watched folder.");
            default:
                return new SetupCheckLine(
                    SetupCheckLine.Unverified,
                    $"{managerLabel} couldn't get an answer from {destination.ClientName} about where it saves {target}, so Weir can't verify those downloads land inside the watched folder.");
        }
    }

    private static SetupCheckLine SaveFolderVerdict(
        string managerLabel, DelunoLibraryDestinations library, DelunoDestination destination, string target, string watched, IFolderProbe? probe)
    {
        var saveFolder = DelunoPathMappings.Apply(library.PathMappings, destination.SaveFolder!);
        if (Inside(saveFolder.Path, watched) || InsideOnceLinksAreFollowed(saveFolder.Path, watched, probe))
        {
            return new SetupCheckLine(
                SetupCheckLine.Ok,
                $"{managerLabel}'s {destination.ClientName} saves {target} in {Seen(saveFolder)}, inside this workflow's watched folder.");
        }

        var fix = destination.SavedBy == DelunoDestination.SavedByClientCategory
            ? $"Set where {destination.ClientName} saves {target} to this workflow's watched folder, or set the watched folder to {saveFolder.Path}."
            : $"{managerLabel} picks this folder for each download from the library's downloads folder. Set the watched folder to {saveFolder.Path}, or change the library's downloads folder in {managerLabel}.";
        return new SetupCheckLine(
            SetupCheckLine.Problem,
            $"{managerLabel}'s {destination.ClientName} saves {target} in {Seen(saveFolder)}, which isn't inside this workflow's watched folder {watched}. {fix}");
    }

    private static SetupCheckLine OutputVerdict(string managerLabel, DelunoLibraryDestinations library, MappedPath processed, string output, IFolderProbe? probe)
    {
        if (output.Length == 0)
        {
            return new SetupCheckLine(
                SetupCheckLine.Problem,
                $"{managerLabel} picks up cleaned files from {Seen(processed)}, but this workflow has no output folder. Set the output folder to {processed.Path}.");
        }

        if (SameFolder(processed.Path, output) || SameOnceLinksAreFollowed(processed.Path, output, probe))
        {
            return new SetupCheckLine(
                SetupCheckLine.Ok,
                $"{managerLabel} picks up cleaned files from {Seen(processed)}, the folder this workflow writes to.");
        }

        var fix = processed.WasMapped
            ? DelunoPathMappingAdvice.WhenMappedElsewhere(managerLabel, processed, "the output folder")
            : DelunoPathMappingAdvice.WhenNoMappingCovers(managerLabel, library.PathMappings, processed.Original, output, "the output folder");
        return new SetupCheckLine(
            SetupCheckLine.Problem,
            $"{managerLabel} picks up cleaned files from {Seen(processed)}, but this workflow writes to {output}. {fix}");
    }
}
