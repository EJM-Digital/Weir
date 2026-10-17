using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// The optional downloaded-scan hand-back: for every enabled Sonarr/Radarr connection linked to a library
/// that has opted in, ask it to run its Downloaded Scan command over a file a live pass just wrote to that
/// library's output folder. Read only towards the manager's remote path mappings, one write towards it (the
/// command); never throws, since a manager that cannot be reached must not fail a pass that already succeeded.
/// A manager that is busy with another scan is not waited for: the next ask is queued as a job to run later
/// (<see cref="DownloadedScanRetryHandler"/>).
/// </summary>
public sealed class DownloadedScanNotifier
{
    private readonly MediaManagerConnectionService _connections;
    private readonly MediaManagerConnectionStore _connectionStore;
    private readonly LibraryStore _libraries;
    private readonly ArrDownloadedScan _scan;
    private readonly ProcessingJobStore? _jobs;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public DownloadedScanNotifier(
        MediaManagerConnectionService connections,
        MediaManagerConnectionStore connectionStore,
        LibraryStore libraries,
        IManagerHttpHandlerFactory handlers,
        ILogger<DownloadedScanNotifier>? logger = null,
        ProcessingJobStore? jobs = null,
        TimeProvider? time = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _connectionStore = connectionStore ?? throw new ArgumentNullException(nameof(connectionStore));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _scan = new ArrDownloadedScan(handlers ?? throw new ArgumentNullException(nameof(handlers)));
        _jobs = jobs;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Notifies every qualifying connection linked to <paramref name="libraryId"/>: enabled, Sonarr or Radarr,
    /// opted in (<c>downloaded_scan_enabled</c>), and covering <paramref name="mediaScope"/>. Commits
    /// <paramref name="uow"/> when it recorded anything.
    /// </summary>
    public async Task NotifyLibraryAsync(
        UnitOfWork uow, long libraryId, string mediaScope, string outputPath, string relativeMediaPath, string? downloadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var wroteAnything = false;
        foreach (var connectionId in await _libraries.ManagerConnectionIdsAsync(uow, libraryId).ConfigureAwait(false))
        {
            if (await QualifyingConnectionAsync(uow, connectionId, mediaScope).ConfigureAwait(false) is not { } connection)
            {
                continue;
            }

            var request = new DownloadedScanRequest(connectionId, mediaScope, outputPath, relativeMediaPath, downloadId);
            await AskAsync(uow, connection, request, cancellationToken).ConfigureAwait(false);
            wroteAnything = true;
        }

        if (wroteAnything)
        {
            await uow.CommitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the connection of <paramref name="request"/> again, after it said it was busy with another scan. A connection that has
    /// since been switched off, removed or opted out is not asked. Commits <paramref name="uow"/> when it recorded anything.
    /// </summary>
    public async Task AskAgainAsync(UnitOfWork uow, DownloadedScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(request);
        if (await QualifyingConnectionAsync(uow, request.ConnectionId, request.ArrScope).ConfigureAwait(false) is not { } connection)
        {
            return;
        }

        await AskAsync(uow, connection, request, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>The connection when it is enabled, Sonarr or Radarr for <paramref name="mediaScope"/>, and opted in to the scan.</summary>
    private async Task<ManagerConnection?> QualifyingConnectionAsync(UnitOfWork uow, long connectionId, string mediaScope)
    {
        var row = await _connectionStore.GetAsync(uow, connectionId).ConfigureAwait(false);
        if (row is null || !row.Enabled || !row.DownloadedScanEnabled)
        {
            return null;
        }

        if (ManagerKindProfiles.ForKind(row.Kind) is not { IsArr: true } profile || profile.ArrScope != mediaScope)
        {
            return null;
        }

        return _connections.ConnectionFromRow(row);
    }

    private async Task AskAsync(UnitOfWork uow, ManagerConnection connection, DownloadedScanRequest request, CancellationToken cancellationToken)
    {
        var result = await _scan.RequestAsync(connection, request.ArrScope, request.OutputPath, request.DownloadId, cancellationToken).ConfigureAwait(false);
        if (result.Busy)
        {
            await AnswerBusyAsync(uow, connection, request, result).ConfigureAwait(false);
            return;
        }

        if (!result.Accepted)
        {
            _logger.LogWarning("Downloaded scan request to {Manager} failed: {Error}", connection.Label, result.FailureReason);
        }

        await RecordAsync(uow, connection, request.RelativeMediaPath, result.Command, result.Accepted, result.FailureReason).ConfigureAwait(false);
    }

    /// <summary>
    /// The manager is busy with another scan. The worker does not wait for it: the next ask is queued to run after a wait that grows
    /// with each busy answer (<see cref="DownloadedScanRetrySchedule"/>), and Activity says so. Once the schedule has run out, or when
    /// there is no queue to ask again from, Activity says Weir has stopped asking.
    /// </summary>
    private async Task AnswerBusyAsync(UnitOfWork uow, ManagerConnection connection, DownloadedScanRequest request, ArrDownloadedScanResult result)
    {
        var busyAnswers = request.BusyAnswers + 1;
        if (_jobs is not null && DownloadedScanRetrySchedule.WaitAfter(busyAnswers, result.RetryAfter) is { } wait)
        {
            await _jobs.EnqueueOrGetAsync(
                uow,
                $"{DownloadedScanRetryHandler.Kind}:{Guid.NewGuid():N}",
                DownloadedScanRetryHandler.Kind,
                (request with { BusyAnswers = busyAnswers }).ToJson(),
                JobQueueRules.DefaultMaxAttempts,
                runnerCost: 0,
                priority: 0,
                notBefore: _time.GetUtcNow() + wait).ConfigureAwait(false);
            var title = DownloadedScanMessages.AskingAgain(connection.Label, FileName(request.RelativeMediaPath));
            await RecordAsync(uow, connection, request.RelativeMediaPath, result.Command, accepted: false, failureReason: null, title, outcome: "retrying").ConfigureAwait(false);
            return;
        }

        var reason = DownloadedScanMessages.StoppedAsking(connection.Label, busyAnswers);
        _logger.LogWarning("Downloaded scan request to {Manager} failed: {Error}", connection.Label, reason);
        await RecordAsync(uow, connection, request.RelativeMediaPath, result.Command, accepted: false, reason).ConfigureAwait(false);
    }

    private static string FileName(string relativeMediaPath) =>
        relativeMediaPath.Length > 0 ? MediaPathNames.Name(relativeMediaPath, OperatingSystem.IsWindows()) : "a file Weir just wrote";

    private static Task<long> RecordAsync(
        UnitOfWork uow, ManagerConnection connection, string relativeMediaPath, WireObject body, bool accepted, string? failureReason,
        string? title = null, string? outcome = null)
    {
        var fileName = FileName(relativeMediaPath);
        title ??= accepted
            ? $"Asked {connection.Label} to scan for {fileName}"
            : $"Weir could not ask {connection.Label} to scan for {fileName}";
        var detail = new WireObject()
            .Set("relative_media_path", relativeMediaPath)
            .Set("manager", connection.Label)
            .Set("command", body)
            .Set("accepted", accepted)
            .Set("trigger", "worker")
            .Set("result", outcome ?? (accepted ? "success" : "failed"));
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
