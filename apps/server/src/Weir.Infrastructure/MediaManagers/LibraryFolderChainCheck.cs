using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// One per-library "folder chain" check: Weir's own watched/work/output folders
/// (<see cref="LibraryFolderChainRules"/>) plus, for every enabled connection that covers the library's media type,
/// whether that manager will actually pick up what Weir writes (<see cref="ManagerSetupCheck"/>, unchanged — this only
/// calls its public method). With no manager connected the Weir-only chain is already a complete, valid setup: an empty
/// manager list can never make the overall result not ready, because <c>List.All</c> over an empty list is true.
/// </summary>
public sealed class LibraryFolderChainCheck
{
    private readonly ManagerSetupCheck _managerSetupCheck;
    private readonly DownloadClientSuggestions _downloadClientSuggestions;
    private readonly LibraryStore _libraries;
    private readonly WeirOptions _options;
    private readonly IFolderProbe _probe;
    private readonly FolderChainAnswers _answers;

    public LibraryFolderChainCheck(
        ManagerSetupCheck managerSetupCheck, DownloadClientSuggestions downloadClientSuggestions, LibraryStore libraries, WeirOptions options, FolderChainAnswers answers)
        : this(managerSetupCheck, downloadClientSuggestions, libraries, options, answers, new FilesystemFolderProbe())
    {
    }

    /// <summary>For tests: the filesystem comes from <paramref name="probe"/> instead of the real disk.</summary>
    internal LibraryFolderChainCheck(
        ManagerSetupCheck managerSetupCheck, DownloadClientSuggestions downloadClientSuggestions, LibraryStore libraries, WeirOptions options, FolderChainAnswers answers, IFolderProbe probe)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(managerSetupCheck);
        ArgumentNullException.ThrowIfNull(downloadClientSuggestions);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        _answers = answers;
        _managerSetupCheck = managerSetupCheck;
        _downloadClientSuggestions = downloadClientSuggestions;
        _libraries = libraries;
        _options = options;
        _probe = probe;
    }

    /// <summary>
    /// One library's chain: <c>{ library_id, local: { ready, lines }, managers: [...], download_clients: [...], ready }</c>.
    /// <c>managers</c> is what <see cref="ManagerSetupCheck.CheckAsync"/> returns for this library's watched/output
    /// folders and media type, for the managers the library is linked to only: a Weir-only library has none, and a
    /// manager it is not linked to never appears in its chain. A library with no id yet (a proposal) is checked against
    /// every manager that covers its media type, since that is what it would be linked to. <c>download_clients</c> is one entry per
    /// enabled bare download-client connection this library actually uses (<see cref="DownloadClientInvolvement"/>), checking
    /// whether any of its folders is this library's watched folder (<see cref="LibraryFolderChainRules.CheckDownloadClientFolders"/>),
    /// one line per folder it saves to. A Weir-only library, or a client it does not use, has none — with none, an empty
    /// list never makes the library not ready, the same as with no manager connected.
    /// </summary>
    public async Task<WireObject> CheckForLibraryAsync(UnitOfWork uow, ProcessingLibraryRecord library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(library);

        var (workFolder, workFolderIsDefault) = WorkFolderOf(library, _options.WeirHome);
        var localLines = LibraryFolderChainRules.CheckLocalFolders(library.WatchedFolder, workFolder, workFolderIsDefault, library.OutputFolder, _probe);
        var localReady = localLines.All(line => line.State != SetupCheckLine.Problem);

        var linked = library.Id == 0
            ? null
            : (IReadOnlySet<long>)(await _libraries.ManagerConnectionIdsAsync(uow, library.Id).ConfigureAwait(false)).ToHashSet();
        // A workflow linked to a manager never removes its originals, so a seeding torrent client is no problem for it.
        var removesOriginals = library.RemoveOriginalAfterSuccess && linked is not { Count: > 0 };
        var managers = await _managerSetupCheck
            .CheckAsync(uow, library.MediaType, library.WatchedFolder, library.OutputFolder, linked, removesOriginals, DelunoLinkOf(library), cancellationToken)
            .ConfigureAwait(false);
        var managersReady = managers.All(entry => entry.Get("ready") is WireBool { Value: true });

        var downloadClients = await DownloadClientEntriesAsync(uow, library, linked, cancellationToken).ConfigureAwait(false);
        var downloadClientsReady = downloadClients.All(entry => entry.Get("ready") is WireBool { Value: true });

        return new WireObject()
            .Set("library_id", library.Id)
            .Set("local", new WireObject()
                .Set("ready", localReady)
                .Set("lines", new WireArray(localLines.Select(line => (WireValue)line.ToOut()))))
            .Set("managers", new WireArray(managers.Select(entry => (WireValue)entry)))
            .Set("download_clients", new WireArray(downloadClients.Select(entry => (WireValue)entry)))
            .Set("ready", localReady && managersReady && downloadClientsReady);
    }

    /// <summary>
    /// A saved workflow's chain, read by a screen or by the server's own look. The answer is remembered, and told to the open
    /// streams when it is not the one last given (<see cref="FolderChainAnswers"/>).
    /// </summary>
    public async Task<WireObject> ReadSavedAsync(UnitOfWork uow, ProcessingLibraryRecord library, CancellationToken cancellationToken)
    {
        var chain = await CheckForLibraryAsync(uow, library, cancellationToken).ConfigureAwait(false);
        _answers.Given(library.Id, chain);
        return chain;
    }

    /// <summary>Forgets the answers of the workflows not in <paramref name="kept"/>, so one that comes back starts afresh.</summary>
    public void ForgetAnswersExcept(IReadOnlySet<long> kept) => _answers.KeepOnly(kept);

    /// <summary>
    /// The library's own folders whose reach can be asked about, each on its own (<see cref="LibraryFolderChainRules.IsUnreachable"/>),
    /// without the media managers and download clients, so it stays cheap enough to ask often.
    /// </summary>
    public static IReadOnlyList<FolderToReach> FoldersToReach(ProcessingLibraryRecord library, string weirHome)
    {
        ArgumentNullException.ThrowIfNull(library);
        var (workFolder, workFolderIsDefault) = WorkFolderOf(library, weirHome);
        return LibraryFolderChainRules.FoldersToReach(library.WatchedFolder, workFolder, workFolderIsDefault, library.OutputFolder);
    }

    /// <summary>The library's work folder, and whether that is Weir's own default because it set none.</summary>
    private static (string Folder, bool IsDefault) WorkFolderOf(ProcessingLibraryRecord library, string weirHome)
    {
        var folderRow = new ProcessingLibraryFolderRow(library.Id, library.MediaType, (int)library.DisplayOrder, library.WorkFolder, library.OutputFolder);
        return (ProcessingLibraryFolders.EffectiveWorkFolder(folderRow, weirHome), string.IsNullOrWhiteSpace(library.WorkFolder));
    }

    /// <summary>The Deluno library the workflow was created from, when it was created from one.</summary>
    private static DelunoLibraryLink? DelunoLinkOf(ProcessingLibraryRecord library) =>
        library.DiscoveredFromConnectionId is { } connectionId && !string.IsNullOrEmpty(library.DiscoveredLibraryKey)
            ? new DelunoLibraryLink(connectionId, library.DiscoveredLibraryKey, library.Name)
            : null;

    private async Task<List<WireObject>> DownloadClientEntriesAsync(
        UnitOfWork uow, ProcessingLibraryRecord library, IReadOnlySet<long>? linkedConnectionIds, CancellationToken cancellationToken)
    {
        var connections = await _downloadClientSuggestions.ReadAllAsync(uow, cancellationToken).ConfigureAwait(false);
        var candidates = connections.Select(item => (item.Row, item.Folders, SavesHere: DownloadClientInvolvement.SavesInto(library.WatchedFolder, item.Folders))).ToList();
        IReadOnlyList<ArrDownloadClientEntry> managerClients = [];
        if (candidates.Any(candidate => !candidate.SavesHere))
        {
            managerClients = await _managerSetupCheck.DownloadClientsUsedAsync(uow, library.MediaType, linkedConnectionIds, cancellationToken).ConfigureAwait(false);
        }

        return [.. candidates
            .Where(candidate => candidate.SavesHere || DownloadClientInvolvement.IsUsedBy(candidate.Row.Kind, candidate.Row.BaseUrl, managerClients))
            .Select(candidate => DownloadClientEntry(candidate.Row, candidate.Folders, library.WatchedFolder))];
    }

    private WireObject DownloadClientEntry(DownloadClientConnectionRecord row, DownloadClientFolders folders, string watchedFolder)
    {
        var label = row.Label;
        var lines = LibraryFolderChainRules.CheckDownloadClientFolders(label, watchedFolder, folders, _probe);
        return new WireObject()
            .Set("connection_id", row.Id)
            .Set("kind", row.Kind)
            .Set("name", row.Name)
            .Set("label", label)
            .Set("ready", lines.All(line => line.State != SetupCheckLine.Problem))
            .Set("lines", new WireArray(lines.Select(line => (WireValue)line.ToOut())));
    }

    /// <summary>
    /// The same chain check for every library linked to one connection (Setup › Connections › Media managers wants "for this
    /// connection, which of its linked libraries are fully chained"), in library order.
    /// </summary>
    public async Task<List<WireObject>> CheckForConnectionAsync(UnitOfWork uow, long connectionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var libraries = await _libraries.LibrariesForConnectionIdAsync(uow, connectionId).ConfigureAwait(false);
        var results = new List<WireObject>();
        foreach (var library in libraries)
        {
            results.Add(await ReadSavedAsync(uow, library, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}
