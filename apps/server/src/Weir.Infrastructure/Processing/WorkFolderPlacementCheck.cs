using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weir.Core.Configuration;
using Weir.Core.Processing;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>
/// Once at startup, says in the log which libraries keep their work folder on a different filesystem from their output
/// folder (#716). There, each finished file is copied to the output folder instead of moved, which on a large file can
/// take as long again as the clean. The default work folder is under Weir's home, which in Docker is usually a different
/// volume from the media, so this is advice only: nothing is changed.
/// </summary>
public sealed class WorkFolderPlacementCheck : BackgroundService
{
    private readonly SqliteDatabase _database;
    private readonly WeirOptions _options;
    private readonly LibraryStore _libraries;
    private readonly ILogger<WorkFolderPlacementCheck> _logger;
    private readonly Func<string, string, bool?> _sameFilesystem;

    public WorkFolderPlacementCheck(SqliteDatabase database, WeirOptions options, LibraryStore libraries, ILogger<WorkFolderPlacementCheck> logger)
        : this(database, options, libraries, logger, FilesystemBoundaries.SameFilesystem)
    {
    }

    /// <summary>For tests: whether two folders share a filesystem comes from <paramref name="sameFilesystem"/>.</summary>
    internal WorkFolderPlacementCheck(
        SqliteDatabase database, WeirOptions options, LibraryStore libraries, ILogger<WorkFolderPlacementCheck> logger, Func<string, string, bool?> sameFilesystem)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sameFilesystem);
        _database = database;
        _options = options;
        _libraries = libraries;
        _logger = logger;
        _sameFilesystem = sameFilesystem;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<ProcessingLibraryRecord> libraries;
        try
        {
            var uow = await UnitOfWork.OpenAsync(_database, stoppingToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                libraries = await _libraries.ListAsync(uow, enabledOnly: true).ConfigureAwait(false);
            }
        }
        catch (SqliteException exception)
        {
            _logger.LogDebug(exception, "Weir could not read the workflows to check where their work folders are.");
            return;
        }

        foreach (var library in libraries.Where(row => !string.IsNullOrWhiteSpace(row.OutputFolder)))
        {
            var work = ProcessingLibraryFolders.ExpandForFilesystem(ProcessingLibraryFolders.EffectiveWorkFolder(
                new ProcessingLibraryFolderRow(library.Id, library.MediaType, 0, library.WorkFolder, library.OutputFolder), _options.WeirHome));
            var output = ProcessingLibraryFolders.ExpandForFilesystem(library.OutputFolder.Trim());
            if (_sameFilesystem(work, output) == false)
            {
                _logger.LogInformation(
                    "{Library}'s work folder {WorkFolder} is on a different filesystem from its output folder {OutputFolder}, so each finished " +
                    "file is copied across instead of moved. Put the work folder on the same volume as the output folder to make that instant.",
                    library.Name,
                    work,
                    output);
            }
        }
    }
}
