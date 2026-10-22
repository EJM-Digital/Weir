using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using static Weir.Api.Endpoints.EndpointLookups;

namespace Weir.Api.Endpoints;

/// <summary>
/// Library mode (#505 point 7): the scheduled scan/clean toggle. Turning it on for the first time shows the same
/// final-removal confirmation Clean does (via the shared helpers on <see cref="LibraryModeMapping"/>), using
/// whatever the last scan found. See <see cref="LibraryModeEndpoints"/> for the rest of the Library mode surface.
/// </summary>
public static class LibraryModeScheduleEndpoints
{
    public static IEndpointRouteBuilder MapLibraryModeScheduleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<LibraryModeScheduleEndpointHandlers>();
        endpoints.MapV1("POST", "/processing/libraries/{library_id}/library-schedule", handlers.PostScheduleAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="LibraryModeScheduleEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class LibraryModeScheduleEndpointHandlers
{
    private readonly LibrarySettingsStore _librarySettings;
    private readonly LibraryScanStore _scans;
    private readonly MediaManagerConnectionService _connections;
    private readonly IHardlinkInspector _hardlinkInspector;
    private readonly RedownloadRiskChecker _riskChecker;
    private readonly LibraryStore _libraries;
    private readonly DataChangePublisher _changes;

    public LibraryModeScheduleEndpointHandlers(
        LibrarySettingsStore librarySettings,
        LibraryScanStore scans,
        MediaManagerConnectionService connections,
        IHardlinkInspector hardlinkInspector,
        RedownloadRiskChecker riskChecker,
        LibraryStore libraries,
        DataChangePublisher changes)
    {
        _librarySettings = librarySettings;
        _scans = scans;
        _connections = connections;
        _hardlinkInspector = hardlinkInspector;
        _riskChecker = riskChecker;
        _libraries = libraries;
        _changes = changes;
    }

    public async Task<ApiResult> PostScheduleAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        var model = new BodyModel(payload, issues);
        var enabled = model.Bool("enabled", false, required: true);
        var confirmed = model.Bool("confirm_final_removal", false);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);
        var settings = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);

        if (enabled && !settings.ScheduleEnabled)
        {
            // #505 point 7: turning the schedule on shows the same final-removal warning once, using whatever the last scan found.
            var files = await _scans.CurrentFilesAsync(uow, libraryId).ConfigureAwait(false);
            var (removingFiles, removingTracks, bytesSaved) = LibraryModeMapping.RemovalTotals(files);
            if (removingFiles > 0 && !confirmed)
            {
                var rules = await LibraryModeRules.ForAsync(uow, _libraries, library, settings).ConfigureAwait(false);
                var connectionsById = await LibraryModeMapping.ConnectionsForFilesAsync(uow, _connections, files).ConfigureAwait(false);
                var preflight = await LibraryCleanPreflightRunner.RunAsync(
                        files, settings, rules, library.MediaType, _hardlinkInspector,
                        _riskChecker, connectionsById, request.Context.RequestAborted)
                    .ConfigureAwait(false);
                return LibraryModeMapping.ConfirmationRequired(removingFiles, removingTracks, bytesSaved, LibraryCleanPreflightRunner.WarningMessages(preflight));
            }
        }

        var updated = settings with { ScheduleEnabled = enabled };
        await _librarySettings.SetAsync(uow, libraryId, updated).ConfigureAwait(false);
        uow.OnCommitted(() => _changes.Publish(DataTopics.LibraryScan));
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(LibraryModeMapping.SettingsOut(updated));
    }
}
