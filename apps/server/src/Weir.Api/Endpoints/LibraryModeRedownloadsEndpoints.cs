using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Library;
using Weir.Core.MediaManagers;
using Weir.Core.Rules;
using Weir.Core.Validation;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using static Weir.Api.Endpoints.EndpointLookups;

namespace Weir.Api.Endpoints;

/// <summary>
/// Library mode (#509): titles a library's current rules would now keep a track for that a past clean removed for
/// good, and the "Download again" action that asks the file's media manager to fetch it once more. See
/// <see cref="LibraryModeEndpoints"/> for the rest of the Library mode surface.
/// </summary>
public static class LibraryModeRedownloadsEndpoints
{
    public static IEndpointRouteBuilder MapLibraryModeRedownloadsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<LibraryModeRedownloadsEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/libraries/{library_id}/library-redownloads", handlers.GetRedownloadsAsync);
        endpoints.MapV1("POST", "/processing/libraries/{library_id}/library-redownloads", handlers.PostRedownloadAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="LibraryModeRedownloadsEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class LibraryModeRedownloadsEndpointHandlers
{
    private readonly LibraryScanStore _scans;
    private readonly IRemovedTrackStore _removedTrackStore;
    private readonly MediaManagerConnectionService _connections;
    private readonly IManagerRedownload _redownload;
    private readonly LibraryStore _libraries;
    private readonly LibrarySettingsStore _librarySettings;
    private readonly IRedownloadTracker _tracker;
    private readonly IActivityWriter _activity;
    private readonly ILogger<LibraryModeRedownloadsEndpointHandlers> _logger;

    public LibraryModeRedownloadsEndpointHandlers(
        LibraryScanStore scans,
        IRemovedTrackStore removedTrackStore,
        MediaManagerConnectionService connections,
        IManagerRedownload redownload,
        LibraryStore libraries,
        LibrarySettingsStore librarySettings,
        IRedownloadTracker tracker,
        IActivityWriter activity,
        ILogger<LibraryModeRedownloadsEndpointHandlers> logger)
    {
        _scans = scans ?? throw new ArgumentNullException(nameof(scans));
        _removedTrackStore = removedTrackStore ?? throw new ArgumentNullException(nameof(removedTrackStore));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _redownload = redownload ?? throw new ArgumentNullException(nameof(redownload));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _librarySettings = librarySettings ?? throw new ArgumentNullException(nameof(librarySettings));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static WireObject RemovedTrackOut(RemovedTrackRecord track) => new WireObject()
        .Set("language", track.Language)
        .Set("type", track.Type == RemovedTrackType.Audio ? "audio" : "subtitle")
        .Set("codec", track.Codec)
        .Set("variant", track.Variant)
        .Set("reason", track.Reason);

    /// <summary>
    /// #509: titles a library's *current* rules would now keep a track for that a past clean removed for good — "12
    /// titles are missing tracks your new rules keep" (issue #509 step 2's diff), scoped to this library. The
    /// "Download again" action <see cref="PostRedownloadAsync"/> offers is gated on <c>can_redownload</c>
    /// (<see cref="ManagerRedownloadRules.CanRedownload"/>): true only for a manager kind issue #509 verified
    /// (Sonarr/Radarr) <em>and</em> a file #551's title matching actually resolved to one of that manager's titles.
    /// </summary>
    public async Task<ApiResult> GetRedownloadsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);
        var settings = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);
        var rules = await LibraryModeRules.ForAsync(uow, _libraries, library, settings).ConfigureAwait(false);

        var allRemoved = await _removedTrackStore.GetAllAsync().ConfigureAwait(false);
        var forLibrary = allRemoved.Where(kv => kv.Key.LibraryId == libraryId).ToDictionary(kv => kv.Key, kv => kv.Value);
        var affected = RemovedTrackDiff.AffectedFiles(rules, forLibrary);

        var scannedByPath = await _scans.FilesAtPathsAsync(uow, libraryId, affected.Select(result => result.File.RelativePath)).ConfigureAwait(false);

        var waiting = (await _tracker.ListWaitingAsync().ConfigureAwait(false)).ToDictionary(item => item.File, item => item.Reason);
        var items = affected.Select(result =>
        {
            var path = result.File.RelativePath;
            scannedByPath.TryGetValue(path, out var scanned);
            var titleName = scanned?.ManagerTitle ?? System.IO.Path.GetFileName(path);
            var canRedownload = ManagerRedownloadRules.CanRedownload(scanned?.ManagerKind, scanned?.ManagerConnectionId, scanned?.ManagerTitleId);
            return new WireObject()
                .Set("path", path)
                .Set("manager_kind", scanned?.ManagerKind)
                .Set("manager_title", scanned?.ManagerTitle)
                .Set("removed_tracks", new WireArray(result.TracksNowWanted.Select(t => (WireValue)RemovedTrackOut(t))))
                .Set("can_redownload", canRedownload)
                .Set("waiting_note", waiting.GetValueOrDefault(result.File))
                .Set("confirmation_message", canRedownload ? ManagerRedownloadRules.DestructiveConfirmation(titleName, null) : null)
                .Set("unavailable_reason", canRedownload
                    ? null
                    : (scanned?.ManagerKind is { } kind && ManagerRedownloadRules.KindSupportsRedownload(kind)
                        ? "Weir does not know which manager file this is, so it cannot ask for a redownload automatically. Scan the library again."
                        : ManagerRedownloadRules.NoManagerMessage));
        }).ToList();

        return ApiRoutes.Ok(new WireObject()
            .Set("library_id", libraryId)
            .Set("titles", new WireArray(items.Select(i => (WireValue)i)))
            .Set("total", items.Count));
    }

    /// <summary>
    /// #509 step 3: asks a manager to redownload one title's file. Requires <c>confirm_destructive</c>
    /// (the operator has seen <see cref="ManagerRedownloadRules.DestructiveConfirmation"/>'s exact wording — the
    /// web only shows this action once <c>GET .../library-redownloads</c> said <c>can_redownload: true</c>).
    /// Resolves the manager connection and title id #551's title matching stored on the latest scan for this
    /// path; answers <see cref="RedownloadOutcome.Unsupported"/> when no scan ever matched it (stale scan, or the
    /// file was cleaned by hand between the two), and <c>"failed"</c> when the manager call itself throws before
    /// committing to anything destructive (a network problem, not a data-safety one).
    /// </summary>
    public async Task<ApiResult> PostRedownloadAsync(ApiRequest request)
    {
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var session = await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        var model = new BodyModel(payload, issues);
        var path = model.Str("path", minLength: 1);
        var confirmedDestructive = model.Bool("confirm_destructive", false, required: true);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        var asker = session.User.Username;
        request.RequireConfirmationToken(csrfToken);

        if (!confirmedDestructive)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "This action deletes the current file before asking for a new one; confirm_destructive must be true.");
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);
        var scanned = (await _scans.FilesAtPathsAsync(uow, libraryId, [path!]).ConfigureAwait(false)).GetValueOrDefault(path!);

        if (!ManagerRedownloadRules.CanRedownload(scanned?.ManagerKind, scanned?.ManagerConnectionId, scanned?.ManagerTitleId))
        {
            await request.CommitAsync().ConfigureAwait(false);
            return await AnswerAsync(new RedownloadAnswer(libraryId, path!, asker, RedownloadAnswer.Unsupported, ManagerRedownloadRules.NoManagerMessage)).ConfigureAwait(false);
        }

        var resolvedConnections = await _connections.ConnectionsByIdAsync(uow, [scanned!.ManagerConnectionId!.Value]).ConfigureAwait(false);
        var connection = resolvedConnections.FirstOrDefault();
        await request.CommitAsync().ConfigureAwait(false);
        if (connection is null)
        {
            return await AnswerAsync(new RedownloadAnswer(libraryId, path!, asker, RedownloadAnswer.Unsupported, ManagerRedownloadRules.NoManagerMessage)).ConfigureAwait(false);
        }

        // Once the manager is asked to delete, the search and the Activity record follow whatever the browser does, so the browser's
        // abort token is not passed on. Each call to the manager is bounded by the manager client's own timeout.
        RedownloadResult result;
        try
        {
            result = await _redownload
                .RequestRedownloadAsync(connection, library.MediaType, scanned.ManagerTitleId!, path, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaManagerHttpException or MediaManagerUnreachableException)
        {
            return await AnswerAsync(new RedownloadAnswer(
                libraryId, path!, asker, RedownloadAnswer.Failed, ManagerDialectRules.Unreachable(connection, exception, "whether it can download this again"),
                connection, library.MediaType)).ConfigureAwait(false);
        }

        return await AnswerAsync(RedownloadAnswer.From(libraryId, path!, asker, result, library.MediaType)).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the answer in Activity, keeps the Library's "waiting for a new copy" mark in step with it, and returns it. The manager
    /// may already have deleted the file by now, so an Activity entry that cannot be written is logged and never hides the answer.
    /// </summary>
    private async Task<ApiResult> AnswerAsync(RedownloadAnswer answer)
    {
        var file = new RemovedTrackFileKey(answer.LibraryId, answer.FilePath);
        if (answer.Outcome == RedownloadAnswer.DeletedButSearchFailed)
        {
            await _tracker.MarkWaitingAsync(file, answer.WaitingNote(), CancellationToken.None).ConfigureAwait(false);
        }
        else if (answer.Outcome == RedownloadAnswer.Requested)
        {
            await _tracker.ClearAsync(file, CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await _activity.RecordAsync(answer.ActivityEvent(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Download again could not record its Activity entry; outcome={Outcome} library_id={LibraryId}", answer.Outcome, answer.LibraryId);
        }

        return ApiRoutes.Ok(new WireObject().Set("path", answer.FilePath).Set("outcome", answer.Outcome).Set("message", answer.Message));
    }
}
