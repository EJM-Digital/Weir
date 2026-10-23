using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>
/// The libraries first-run setup can offer once a media manager or download client is connected: one per media type, with
/// the watched folder the connection already knows and a default output folder beside it. Composes what the library editor
/// already reads (<see cref="ManagerSetupCheck.SuggestFoldersAsync"/> for Sonarr, Radarr and Deluno,
/// <see cref="DownloadClientSuggestions"/> for bare download clients) and only ever reads: nothing is created, and nothing
/// on a manager or download client is changed.
/// </summary>
/// <remarks>
/// A folder is offered only when it is one library's own: never a download client's default completed folder, which every
/// media type saves to, and never one that overlaps a workflow already set up or another offer.
/// A new install starts with an empty Movies and an empty TV library, so an offer fills one of those in when its media
/// type still has one with no folders, and is a new library only when the first library of that type is already in use.
/// </remarks>
public sealed class LibrarySuggestions
{
    private readonly ManagerSetupCheck _managerSetup;
    private readonly DownloadClientSuggestions _downloadClients;
    private readonly LibraryStore _libraries;

    public LibrarySuggestions(ManagerSetupCheck managerSetup, DownloadClientSuggestions downloadClients, LibraryStore libraries)
    {
        ArgumentNullException.ThrowIfNull(managerSetup);
        ArgumentNullException.ThrowIfNull(downloadClients);
        ArgumentNullException.ThrowIfNull(libraries);
        _managerSetup = managerSetup;
        _downloadClients = downloadClients;
        _libraries = libraries;
    }

    public async Task<LibrarySuggestionSet> SuggestAsync(UnitOfWork uow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var clients = await _downloadClients.ReadAllAsync(uow, cancellationToken).ConfigureAwait(false);
        var existing = await _libraries.ListAsync(uow).ConfigureAwait(false);
        var takenNames = existing.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        var libraries = new List<SuggestedLibrary>();
        var notes = new List<string>();

        foreach (var scope in ProcessingMediaScopes.All)
        {
            var managers = await _managerSetup.SuggestFoldersAsync(uow, scope, cancellationToken).ConfigureAwait(false);
            if (Choose(scope, managers, clients) is not { } source)
            {
                notes.AddRange(managers.Select(manager => manager.Problem).OfType<string>());
                notes.AddRange(clients
                    .Where(client => !string.IsNullOrEmpty(client.Folders.CompletedFolder))
                    .Select(client => LibraryFolderSuggestionRules.SharedClientFolderNote(client.Row.Label, scope)));
                continue;
            }

            if (libraries.FirstOrDefault(library => Overlap(library.WatchedFolder, source.Watched)) is { } sharing)
            {
                notes.Add($"{source.Label} saves {sharing.Name} and {LibraryFolderSuggestionRules.LibraryName(scope)} downloads in the same folder, or one inside the other, so Weir suggests one workflow for them. Add another in Setup › Workflows once they are kept apart.");
                continue;
            }

            if (existing.FirstOrDefault(row => Overlap(row.WatchedFolder, source.Watched)) is { } watching)
            {
                var libraryName = LibraryFolderSuggestionRules.LibraryName(scope);
                notes.Add(watching.MediaType == scope && SameFolder(watching.WatchedFolder, source.Watched)
                    ? $"{watching.Name} already watches {watching.WatchedFolder}, the folder {source.Label} reports for {libraryName}, so there is nothing to add."
                    : $"{watching.Name} already watches {watching.WatchedFolder}, which overlaps the {source.Watched} that {source.Label} reports for {libraryName}, so Weir suggests no other workflow for it.");
                continue;
            }

            var empty = await EmptyLibraryAsync(uow, scope).ConfigureAwait(false);
            var name = empty?.Name ?? LibraryDiscoveryService.UniqueName(takenNames, LibraryFolderSuggestionRules.LibraryName(scope));
            takenNames.Add(name);
            libraries.Add(new SuggestedLibrary(
                empty?.Id,
                name,
                scope,
                source.Watched,
                source.Output ?? LibraryFolderSuggestionRules.DefaultOutputFolder(source.Watched, scope) ?? string.Empty,
                source.Label,
                [.. managers.Select(manager => manager.ConnectionId)]));
        }

        return new LibrarySuggestionSet(libraries, [.. notes.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>The first library of a media type, when nothing has been set up in it yet.</summary>
    private async Task<ProcessingLibraryRecord?> EmptyLibraryAsync(UnitOfWork uow, string scope) =>
        await _libraries.SeededForScopeAsync(uow, scope).ConfigureAwait(false) is { WatchedFolder.Length: 0, OutputFolder.Length: 0 } first ? first : null;

    /// <summary>A media manager's own folder first, since it is the one that hands files over; a bare download client's after it.</summary>
    private static FolderSource? Choose(
        string scope,
        IReadOnlyList<ManagerFolderSuggestion> managers,
        IReadOnlyList<(DownloadClientConnectionRecord Row, DownloadClientFolders Folders)> clients)
    {
        if (managers.FirstOrDefault(manager => !string.IsNullOrEmpty(manager.WatchedFolder)) is { } manager)
        {
            return new FolderSource(manager.Label, manager.WatchedFolder!, manager.OutputFolder);
        }

        foreach (var (row, folders) in clients)
        {
            if (LibraryFolderSuggestionRules.DownloadClientFolderFor(scope, folders) is { } folder)
            {
                return new FolderSource(row.Label, folder, null);
            }
        }

        return null;
    }

    private static bool SameFolder(string first, string second) =>
        LibraryRules.NormalizeFolder(first) == LibraryRules.NormalizeFolder(second);

    /// <summary>Whether two watched folders are the same or one is inside the other, which two workflows may not be.</summary>
    private static bool Overlap(string first, string second) =>
        LibraryRules.NormalizeFolder(first) is { } normalizedFirst
        && LibraryRules.NormalizeFolder(second) is { } normalizedSecond
        && LibraryRules.FoldersOverlap(normalizedFirst, normalizedSecond);

    private sealed record FolderSource(string Label, string Watched, string? Output);
}
