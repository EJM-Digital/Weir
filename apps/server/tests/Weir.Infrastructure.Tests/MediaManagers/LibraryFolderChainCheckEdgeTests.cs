using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>The folder chain of a workflow that keeps its originals, and of one made from a manager's library that has no key to name it by.</summary>
public sealed class LibraryFolderChainCheckEdgeTests : IDisposable
{
    private readonly MediaManagerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private LibraryFolderChainCheck Check()
    {
        var clients = new DownloadClientConnectionService(_fixture.Cipher, new DownloadClientConnectionStore());
        var suggestions = new DownloadClientSuggestions(clients, new DownloadClientConnectionStore(), new DownloadClientPorts([]));
        var setup = new ManagerSetupCheck(_fixture.Connections, _fixture.ConnectionStore, _fixture.Http, new FilesystemFolderProbe(), NullLogger<ManagerSetupCheck>.Instance);
        return new LibraryFolderChainCheck(setup, suggestions, _fixture.Libraries, _fixture.Store.Options, new FolderChainAnswers(new DataChangePublisher()));
    }

    private async Task<ProcessingLibraryRecord> SeededAsync() =>
        (await _fixture.Db(uow => _fixture.Libraries.SeededForScopeAsync(uow, "movie"), commit: false))!;

    private Task<WireObject> ChainOfAsync(ProcessingLibraryRecord library) =>
        _fixture.Db(uow => Check().CheckForLibraryAsync(uow, library, CancellationToken.None), commit: false);

    [Fact]
    public async Task A_workflow_that_keeps_its_originals_has_a_chain_like_any_other()
    {
        var library = (await SeededAsync()) with { RemoveOriginalAfterSuccess = false };

        var chain = await ChainOfAsync(library);

        Assert.Equal(library.Id, ((WireInteger)chain["library_id"]).Value);
    }

    [Fact]
    public async Task A_workflow_made_from_a_manager_library_that_names_no_key_is_checked_as_one_made_by_hand()
    {
        var connection = await _fixture.AddConnectionAsync("deluno");
        var library = (await SeededAsync()) with { DiscoveredFromConnectionId = connection, DiscoveredLibraryKey = string.Empty };

        var chain = await ChainOfAsync(library);

        Assert.Equal(library.Id, ((WireInteger)chain["library_id"]).Value);
    }
}
