using Weir.Core.Json;

namespace Weir.Core.MediaManagers;

public static partial class ManagerSetupRules
{
    /// <summary>
    /// The Deluno library a workflow is checked against. <paramref name="preferredKey"/> is the library the workflow was
    /// created from, when it was, and is used whenever Deluno still has it; otherwise the first library for the media type
    /// that is set to Refine before import. With no such library <see cref="DelunoLibraryChoice.Library"/> is null and
    /// <see cref="DelunoLibraryChoice.Lines"/> says why.
    /// </summary>
    public static DelunoLibraryChoice ChooseDelunoLibrary(
        string managerLabel, string mediaScope, IReadOnlyList<ManagerLibraryDescriptor> libraries, string? preferredKey = null)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var scopeWord = DelunoDestinationNotices.ScopeWord(mediaScope);
        if (preferredKey is not null && libraries.All(library => library.Key != preferredKey))
        {
            return new DelunoLibraryChoice(null, [new SetupCheckLine(SetupCheckLine.Unverified, DelunoDestinationNotices.LibraryGone(managerLabel))]);
        }

        var preferred = libraries.FirstOrDefault(library => library.Key == preferredKey && library.MediaScope == mediaScope);
        if (preferred is { ProcessesBeforeImport: false })
        {
            return new DelunoLibraryChoice(null, [new SetupCheckLine(
                SetupCheckLine.Problem,
                $"{DelunoDestinationNotices.StoppedRefining(managerLabel, preferred.Name, mediaScope)} {DelunoDestinationNotices.StoppedRefiningAdvice(managerLabel)}")]);
        }

        var refining = libraries.Where(library => library.MediaScope == mediaScope && library.ProcessesBeforeImport).ToList();
        if (refining.Count == 0)
        {
            return new DelunoLibraryChoice(null, [new SetupCheckLine(
                SetupCheckLine.Problem,
                $"No {managerLabel} {scopeWord} library is set to Refine before import, so {managerLabel} will not hand {scopeWord} downloads to Weir. " +
                $"Choose Refine before import for that library in {managerLabel}.")]);
        }

        var chosen = preferred ?? refining[0];
        return refining.Count > 1 && preferred is null
            ? new DelunoLibraryChoice(chosen, [new SetupCheckLine(
                SetupCheckLine.Note,
                $"{managerLabel} has {refining.Count} {scopeWord} libraries set to Refine before import; the folders below are {chosen.Name}'s. " +
                "Give each its own Weir workflow.")])
            : new DelunoLibraryChoice(chosen, []);
    }

    /// <summary>
    /// What Deluno's manifest (<c>GET /api/integrations/external/manifest</c>) alone can say. It carries each library's
    /// workflow, its <c>downloadsPath</c> ("downloads arrive in") and its <c>processorOutputPath</c>, but not Deluno's path
    /// mappings and not where a download client saves. A hand-off's file has to sit inside the watched folder
    /// (<see cref="HandoffPaths.RelativeMediaPathForHandoff"/>), compared the same case- and separator-insensitive way
    /// here and, when <paramref name="probe"/> is given, after following any junction or link, so one folder reached two
    /// ways is one folder. A reported folder that is still not Weir's cannot be told apart from one Deluno maps, so it is
    /// <see cref="SetupCheckLine.Unverified"/> with both ways to fix it, never a pass and never a failure, and each enabled
    /// client gets its own <see cref="SetupCheckLine.Unverified"/> line. A Deluno that publishes its download destinations
    /// is judged by <see cref="EvaluateDelunoWithDestinations"/>.
    /// </summary>
    public static DelunoSetupResult EvaluateDeluno(
        string managerLabel,
        string mediaScope,
        string watchedFolder,
        string outputFolder,
        IReadOnlyList<ManagerLibraryDescriptor> libraries,
        IReadOnlyList<ManagerDownloadClientDescriptor>? downloadClients = null,
        IFolderProbe? probe = null)
    {
        var choice = ChooseDelunoLibrary(managerLabel, mediaScope, libraries);
        return EvaluateDelunoManifest(managerLabel, mediaScope, watchedFolder, outputFolder, choice, downloadClients, probe);
    }

    /// <summary><see cref="EvaluateDeluno(string, string, string, string, IReadOnlyList{ManagerLibraryDescriptor}, IReadOnlyList{ManagerDownloadClientDescriptor}?, IFolderProbe?)"/> for a library already chosen.</summary>
    internal static DelunoSetupResult EvaluateDelunoManifest(
        string managerLabel,
        string mediaScope,
        string watchedFolder,
        string outputFolder,
        DelunoLibraryChoice choice,
        IReadOnlyList<ManagerDownloadClientDescriptor>? downloadClients,
        IFolderProbe? probe)
    {
        if (choice.Library is not { } library)
        {
            return new DelunoSetupResult(null, null, choice.Lines);
        }

        var lines = new List<SetupCheckLine>(choice.Lines);
        var downloads = WireStrings.Strip(library.DownloadsPath ?? string.Empty);
        var watched = WireStrings.Strip(watchedFolder);
        lines.Add(DownloadsLine(managerLabel, library.Name, downloads, watched, probe));
        lines.AddRange(DownloadClientLines(managerLabel, mediaScope, downloadClients ?? []));

        var processed = WireStrings.Strip(library.OutputPath ?? string.Empty);
        var output = WireStrings.Strip(outputFolder);
        if (processed.Length > 0)
        {
            lines.Add(OutputLine(managerLabel, processed, output, probe));
        }

        return new DelunoSetupResult(downloads.Length > 0 ? downloads : null, processed.Length > 0 ? processed : null, lines);
    }

    /// <summary>Whether the folder Deluno says downloads arrive in is inside Weir's watched folder, and what to do when it is not.</summary>
    private static SetupCheckLine DownloadsLine(string managerLabel, string libraryName, string downloads, string watched, IFolderProbe? probe)
    {
        if (downloads.Length == 0)
        {
            return new SetupCheckLine(
                SetupCheckLine.Unverified,
                $"{managerLabel} does not say where {libraryName}'s downloads arrive, so Weir cannot verify that its hand-offs sit inside this workflow's watched folder. " +
                $"Set the downloads folder in {managerLabel} (or the clients' category folders) and Weir will pick it up.");
        }

        if (watched.Length == 0)
        {
            return new SetupCheckLine(
                SetupCheckLine.Problem,
                $"{managerLabel} reports {libraryName}'s downloads in {downloads}, but this workflow has no watched folder. Set the watched folder to {downloads}.");
        }

        const string NoClientFolder = "Weir can't see where each download client really saves.";
        if (Inside(downloads, watched))
        {
            return new SetupCheckLine(
                SetupCheckLine.Unverified,
                $"{managerLabel} says its {libraryName} library downloads to {downloads} (inside Weir's watched folder). {NoClientFolder}");
        }

        return InsideOnceLinksAreFollowed(downloads, watched, probe)
            ? new SetupCheckLine(
                SetupCheckLine.Unverified,
                $"{managerLabel} says its {libraryName} library downloads to {downloads}, which leads into Weir's watched folder {watched}. {NoClientFolder}")
            : new SetupCheckLine(
                SetupCheckLine.Unverified,
                $"{managerLabel} reports {libraryName}'s downloads in {downloads}, which isn't inside this workflow's watched folder {watched} as Weir sees it. " +
                DelunoPathMappingAdvice.For(managerLabel, downloads, watched, "the watched folder"));
    }

    /// <summary>Whether the folder Deluno picks cleaned files up from is Weir's output folder, and what to do when it is not.</summary>
    private static SetupCheckLine OutputLine(string managerLabel, string processed, string output, IFolderProbe? probe)
    {
        if (output.Length == 0)
        {
            return new SetupCheckLine(
                SetupCheckLine.Problem,
                $"{managerLabel} picks up cleaned files from {processed}, but this workflow has no output folder. Set the output folder to {processed}.");
        }

        if (SameFolder(processed, output))
        {
            return new SetupCheckLine(SetupCheckLine.Ok, $"{managerLabel} picks up cleaned files from {processed}, the folder this workflow writes to.");
        }

        return SameOnceLinksAreFollowed(processed, output, probe)
            ? new SetupCheckLine(SetupCheckLine.Ok, $"{managerLabel} picks up cleaned files from {processed}, which leads to the folder this workflow writes to, {output}.")
            : new SetupCheckLine(
                SetupCheckLine.Unverified,
                $"{managerLabel} picks up cleaned files from {processed}, which isn't this workflow's output folder {output} as Weir sees it. " +
                DelunoPathMappingAdvice.For(managerLabel, processed, output, "the output folder"));
    }

    private static bool InsideOnceLinksAreFollowed(string path, string folder, IFolderProbe? probe) =>
        probe?.ResolveFinalPath(path) is { } finalPath && probe.ResolveFinalPath(folder) is { } finalFolder && Inside(finalPath, finalFolder);

    private static bool SameOnceLinksAreFollowed(string first, string second, IFolderProbe? probe) =>
        probe?.ResolveFinalPath(first) is { } finalFirst && probe.ResolveFinalPath(second) is { } finalSecond && SameFolder(finalFirst, finalSecond);

    private static bool SameFolder(string first, string second) => Inside(first, second) && Inside(second, first);

    /// <summary>One unverified line per enabled Deluno download client: the category it files this media type under, and no folder to check.</summary>
    private static IEnumerable<SetupCheckLine> DownloadClientLines(
        string managerLabel, string mediaScope, IReadOnlyList<ManagerDownloadClientDescriptor> downloadClients)
    {
        foreach (var client in downloadClients.Where(client => client.Enabled))
        {
            var category = mediaScope == MediaManagerKinds.Tv ? client.TvCategory : client.MoviesCategory;
            var filedUnder = category is null ? "its downloads" : $"the category \"{category}\"";
            yield return new SetupCheckLine(
                SetupCheckLine.Unverified,
                $"{managerLabel}'s {client.Name} files this workflow's downloads under {filedUnder}, but {managerLabel} does not publish where it saves them. " +
                "Weir cannot verify they land inside the watched folder.");
        }
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it: case- and separator-insensitive, as <c>HandoffPaths</c> compares.</summary>
    private static bool Inside(string path, string folder)
    {
        var target = Comparable(path);
        var root = Comparable(folder);
        return root.Length > 0 && (target == root || target.StartsWith(root + "/", StringComparison.Ordinal));
    }

    private static string Comparable(string path) => WireStrings.Strip(path.Replace('\\', '/')).TrimEnd('/').ToLowerInvariant();
}
