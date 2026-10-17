using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// The optional downloaded-scan hand-back: for every enabled Sonarr/Radarr connection linked to a library
/// that has opted in, ask it to run its Downloaded Scan command over a file a live pass just wrote to that
/// library's output folder. Read only towards the manager's remote path mappings, one write towards it (the
/// command); never throws, since a manager that cannot be reached must not fail a pass that already succeeded.
/// </summary>
public sealed class DownloadedScanNotifier
{
    private readonly MediaManagerConnectionService _connections;
    private readonly MediaManagerConnectionStore _connectionStore;
    private readonly LibraryStore _libraries;
    private readonly ArrDownloadedScan _scan;
    private readonly ILogger _logger;

    public DownloadedScanNotifier(
        MediaManagerConnectionService connections,
        MediaManagerConnectionStore connectionStore,
        LibraryStore libraries,
        IManagerHttpHandlerFactory handlers,
        ILogger<DownloadedScanNotifier>? logger = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _connectionStore = connectionStore ?? throw new ArgumentNullException(nameof(connectionStore));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _scan = new ArrDownloadedScan(handlers ?? throw new ArgumentNullException(nameof(handlers)));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Notifies every qualifying connection linked to <paramref name="libraryId"/>: enabled, Sonarr or Radarr,
    /// opted in (<c>downloaded_scan_enabled</c>), and covering <paramref name="mediaScope"/>. Commits
    /// <paramref name="uow"/> when it recorded anything.
    /// </summary>
    public async Task NotifyLibraryAsync(
        UnitOfWork uow, long libraryId, string mediaScope, string outputPath, string relativeMediaPath, int? downloadClientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var wroteAnything = false;
        foreach (var connectionId in await _libraries.ManagerConnectionIdsAsync(uow, libraryId).ConfigureAwait(false))
        {
            var row = await _connectionStore.GetAsync(uow, connectionId).ConfigureAwait(false);
            if (row is null || !row.Enabled || !row.DownloadedScanEnabled)
            {
                continue;
            }

            if (ManagerKindProfiles.ForKind(row.Kind) is not { IsArr: true } profile || profile.ArrScope != mediaScope)
            {
                continue;
            }

            if (_connections.ConnectionFromRow(row) is not { } connection)
            {
                continue;
            }

            await NotifyOneAsync(uow, connection, profile.ArrScope, outputPath, relativeMediaPath, downloadClientId, cancellationToken).ConfigureAwait(false);
            wroteAnything = true;
        }

        if (wroteAnything)
        {
            await uow.CommitAsync().ConfigureAwait(false);
        }
    }

    private async Task NotifyOneAsync(
        UnitOfWork uow, ManagerConnection connection, string arrScope, string outputPath, string relativeMediaPath, int? downloadClientId,
        CancellationToken cancellationToken)
    {
        var result = await _scan.RequestAsync(connection, arrScope, outputPath, downloadClientId, cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
        {
            _logger.LogWarning("Downloaded scan request to {Manager} failed: {Error}", connection.Label, result.FailureReason);
        }

        await RecordAsync(uow, connection, relativeMediaPath, result.Command, result.Accepted, result.FailureReason).ConfigureAwait(false);
    }

    private static Task<long> RecordAsync(UnitOfWork uow, ManagerConnection connection, string relativeMediaPath, WireObject body, bool accepted, string? failureReason)
    {
        var fileName = relativeMediaPath.Length > 0 ? MediaPathNames.Name(relativeMediaPath, OperatingSystem.IsWindows()) : "a file Weir just wrote";
        var title = accepted
            ? $"Asked {connection.Label} to scan for {fileName}"
            : $"Weir could not ask {connection.Label} to scan for {fileName}";
        var detail = new WireObject()
            .Set("relative_media_path", relativeMediaPath)
            .Set("manager", connection.Label)
            .Set("command", body)
            .Set("accepted", accepted)
            .Set("trigger", "worker")
            .Set("result", accepted ? "success" : "failed");
        if (failureReason is not null)
        {
            detail.Set("reason", failureReason);
        }

        return SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
            ActivityEventTypes.ProcessingDownloadedScanRequested,
            "processing",
            title,
            WireStrings.Slice(WireJsonWriter.Dumps(detail, WireJsonFormat.Compact), 10_000)));
    }
}
