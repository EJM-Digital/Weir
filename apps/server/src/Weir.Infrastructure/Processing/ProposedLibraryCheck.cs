using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>The folders a library is about to be created with.</summary>
public sealed record ProposedLibrary(string MediaType, string WatchedFolder, string OutputFolder);

/// <summary>
/// What creating libraries with these folders would run into, before any is created: the library rules that would refuse a
/// folder (<see cref="LibraryRules.ValidateFolders"/>, applied exactly as a create applies it, against the saved libraries
/// and against the other proposals) and, for a folder that passes, the same folder chain check a saved library gets. Reads
/// only: nothing is saved.
/// </summary>
public sealed class ProposedLibraryCheck
{
    private readonly LibraryStore _libraries;
    private readonly LibraryFolderChainCheck _folderChain;
    private readonly WeirOptions _options;

    public ProposedLibraryCheck(LibraryStore libraries, LibraryFolderChainCheck folderChain, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(folderChain);
        ArgumentNullException.ThrowIfNull(options);
        _libraries = libraries;
        _folderChain = folderChain;
        _options = options;
    }

    /// <summary>
    /// One entry per proposal, in order: <c>{ problem, chain }</c>. <c>problem</c> is the sentence a create would be refused
    /// with, or null; <c>chain</c> is the folder chain (with <c>library_id</c> 0, as nothing is saved yet), or null when there
    /// is a problem.
    /// </summary>
    public async Task<List<WireObject>> CheckAsync(UnitOfWork uow, IReadOnlyList<ProposedLibrary> proposals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(proposals);
        var saved = await _libraries.OtherFoldersAsync(uow, excludeId: null).ConfigureAwait(false);
        var results = new List<WireObject>();
        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            var others = saved.Concat(proposals.Where((_, other) => other != index).Select(OtherFolders)).ToList();
            var problem = RefusalFor(proposal, others);
            var chain = problem is null
                ? await _folderChain.CheckForLibraryAsync(uow, Record(proposal), cancellationToken).ConfigureAwait(false)
                : null;
            results.Add(new WireObject().Set("problem", problem).Set("chain", chain ?? (WireValue)WireNull.Instance));
        }

        return results;
    }

    private string? RefusalFor(ProposedLibrary proposal, IReadOnlyList<OtherLibraryFolders> others)
    {
        try
        {
            LibraryRules.ValidateFolders(proposal.WatchedFolder, workFolder: null, proposal.OutputFolder, others, _options.WeirHome);
            return null;
        }
        catch (ProcessingLibraryException exception)
        {
            return exception.Message;
        }
    }

    private static OtherLibraryFolders OtherFolders(ProposedLibrary proposal) =>
        new(0, LibraryFolderSuggestionRules.LibraryName(proposal.MediaType), proposal.WatchedFolder, proposal.OutputFolder);

    private static ProcessingLibraryRecord Record(ProposedLibrary proposal) => new()
    {
        Name = LibraryFolderSuggestionRules.LibraryName(proposal.MediaType),
        MediaType = proposal.MediaType,
        WatchedFolder = proposal.WatchedFolder,
        OutputFolder = proposal.OutputFolder,
    };
}
