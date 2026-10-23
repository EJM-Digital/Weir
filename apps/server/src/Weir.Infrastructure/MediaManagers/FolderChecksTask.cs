using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// <c>folder-checks</c>: while a browser is watching, checks each switched-on workflow's folder chain
/// (<see cref="LibraryFolderChainCheck"/>) on its own and says so on <see cref="DataTopics.FolderChecks"/> when any answer is not
/// the one anyone was given last time (<see cref="FolderChainAnswers"/>), so a folder that goes missing, or comes back, turns Health
/// red or green without anyone asking. Each look is told to the open streams as the time Health shows (<see cref="ServerLooks"/>). Idle, with no stream open, it does not
/// touch the disk, the database or a media manager.
/// </summary>
public sealed class FolderChecksTask : IPeriodicTask
{
    private readonly SqliteDatabase _database;
    private readonly LibraryStore _libraries;
    private readonly LibraryFolderChainCheck _check;
    private readonly ActivityStreamClients _clients;
    private readonly ServerLooks _looks;

    public FolderChecksTask(
        SqliteDatabase database, LibraryStore libraries, LibraryFolderChainCheck check, ActivityStreamClients clients, ServerLooks looks)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(looks);
        _database = database;
        _libraries = libraries;
        _check = check;
        _clients = clients;
        _looks = looks;
    }

    public string Name => "folder-checks";

    public string? Label => null;

    public TimeSpan Interval => TimeSpan.FromSeconds(15);

    public bool RunAtStart => false;

    public TimeSpan? FailureCooldown => null;

    public string FailureMessage => "Weir could not check its workflows' folders.";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (_clients.Count == 0)
        {
            return;
        }

        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var enabled = await _libraries.ListAsync(uow, enabledOnly: true).ConfigureAwait(false);
            foreach (var library in enabled)
            {
                await _check.ReadSavedAsync(uow, library, cancellationToken).ConfigureAwait(false);
            }

            _check.ForgetAnswersExcept(enabled.Select(library => library.Id).ToHashSet());
        }

        _looks.FoldersLooked();
    }
}
