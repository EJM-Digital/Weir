using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;
using static Weir.Api.Endpoints.EndpointLookups;

namespace Weir.Api.Endpoints;

/// <summary>Processing libraries — <c>/api/v1/processing/libraries</c>. Manager coverage reads the linked
/// connections' saved test results (#520). Also the opt-in Reject failure policy's support gate
/// (<c>GET /processing/reject-support</c>, and the same check on save; #522 part 4), and the manager-setup
/// check used while a library is being created or edited. Media-manager library discovery (discover/drift/
/// import) and library unlink (#554) live in <see cref="ProcessingLibraryDiscoveryEndpoints"/>; rule sets
/// live in <see cref="ProcessingRuleSetsEndpoints"/>. Request/response mapping is shared via
/// <see cref="ProcessingLibraryMapping"/>.</summary>
public static class ProcessingLibraryEndpoints
{
    public static IEndpointRouteBuilder MapProcessingLibraryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingLibraryEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/libraries", handlers.GetLibrariesAsync);
        endpoints.MapV1("POST", "/processing/libraries", handlers.PostLibraryAsync);
        endpoints.MapV1("GET", "/processing/reject-support", handlers.GetRejectSupportAsync);
        endpoints.MapV1("GET", "/processing/manager-setup", handlers.GetManagerSetupAsync);
        endpoints.MapV1("GET", "/processing/libraries/{library_id}/folder-chain", handlers.GetLibraryFolderChainAsync);
        endpoints.MapV1("GET", "/processing/libraries/{library_id}", handlers.GetLibraryAsync);
        endpoints.MapV1("PUT", "/processing/libraries/{library_id}", handlers.PutLibraryAsync);
        endpoints.MapV1("DELETE", "/processing/libraries/{library_id}", handlers.DeleteLibraryAsync);
        endpoints.MapV1("POST", "/processing/libraries/reorder", handlers.PostReorderAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingLibraryEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingLibraryEndpointHandlers
{
    private readonly LibraryStore _libraries;
    private readonly MediaManagerConnectionStore _connectionStore;
    private readonly MediaManagerConnectionService _connections;
    private readonly OperatorSettingsStore _operatorSettings;
    private readonly SuiteSettingsStore _suiteSettings;
    private readonly ScanWakeups _scanWakeups;
    private readonly RejectSupportEvaluator _rejectSupport;
    private readonly ManagerSetupCheck _managerSetupCheck;
    private readonly LibraryFolderChainCheck _folderChainCheck;
    private readonly ScanSettingsChanges _scanSettingsChanges;
    private readonly LibrarySettingsStore _librarySettings;

    public ProcessingLibraryEndpointHandlers(
        LibraryStore libraries,
        MediaManagerConnectionStore connectionStore,
        MediaManagerConnectionService connections,
        OperatorSettingsStore operatorSettings,
        SuiteSettingsStore suiteSettings,
        ScanWakeups scanWakeups,
        RejectSupportEvaluator rejectSupport,
        ManagerSetupCheck managerSetupCheck,
        LibraryFolderChainCheck folderChainCheck,
        ScanSettingsChanges scanSettingsChanges,
        LibrarySettingsStore librarySettings)
    {
        _libraries = libraries;
        _connectionStore = connectionStore;
        _connections = connections;
        _operatorSettings = operatorSettings;
        _suiteSettings = suiteSettings;
        _scanWakeups = scanWakeups;
        _rejectSupport = rejectSupport;
        _managerSetupCheck = managerSetupCheck;
        _folderChainCheck = folderChainCheck;
        _scanSettingsChanges = scanSettingsChanges;
        _librarySettings = librarySettings;
    }

    private Task<WireObject> LibraryOutAsync(ApiRequest request, Weir.Infrastructure.Sqlite.UnitOfWork uow, ProcessingLibraryRecord row) =>
        ProcessingLibraryMapping.LibraryOutAsync(request, uow, row, _libraries, _connectionStore, _operatorSettings, _suiteSettings, _scanWakeups);

    public async Task<ApiResult> GetLibrariesAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var rows = await _libraries.ListAsync(uow).ConfigureAwait(false);
        var items = new List<WireValue>();
        foreach (var row in rows)
        {
            items.Add(await LibraryOutAsync(request, uow, row).ConfigureAwait(false));
        }

        return ApiRoutes.Ok(new WireArray(items));
    }

    public async Task<ApiResult> GetLibraryAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("library_id", issues);
        issues.ThrowIfAny();
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(await LibraryOutAsync(request, uow, await RequireLibraryAsync(uow, _libraries, id).ConfigureAwait(false)).ConfigureAwait(false));
    }

    /// <summary>
    /// <c>GET /api/v1/processing/reject-support</c>: whether the opt-in Reject failure policy can be chosen for a library
    /// linked to the given connections. Asks each manager's manifest (or its static capabilities, for one whose port
    /// removes queue items), so it is called when the option is shown, not on every list.
    /// </summary>
    public async Task<ApiResult> GetRejectSupportAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var connectionIds = new List<long>();
        foreach (var raw in request.Context.Request.Query["connection_ids"])
        {
            if (long.TryParse(raw, out var id))
            {
                connectionIds.Add(id);
            }
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var connections = await _connections.ConnectionsByIdAsync(uow, connectionIds).ConfigureAwait(false);
        var support = await _rejectSupport.EvaluateAsync(connections).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("available", support.Available).Set("reason", support.Reason));
    }

    /// <summary>
    /// The connections a workflow is linked to, from the repeated <c>connection_ids</c> query parameter; null when it is
    /// absent (every connection that covers the media type), an empty set when it is present but names none.
    /// </summary>
    private static HashSet<long>? LinkedConnectionIds(ApiRequest request)
    {
        var raw = request.Context.Request.Query["connection_ids"];
        if (raw.Count == 0)
        {
            return null;
        }

        return [.. raw.Select(value => long.TryParse(value, out var id) ? id : (long?)null).OfType<long>()];
    }

    /// <summary>
    /// <c>GET /api/v1/processing/manager-setup</c>: for a library's media type and folders (saved or still being typed),
    /// what each enabled Sonarr, Radarr or Deluno connection needs, and whether it already has it — the remote path
    /// mapping Sonarr/Radarr must hold, or the folders Deluno reports. Read only: Weir never writes a manager's settings.
    /// </summary>
    public async Task<ApiResult> GetManagerSetupAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var mediaType = ProcessingMediaScopes.Movie;
        if (request.Query("media_type") is not { } rawType)
        {
            issues.Add(new ValidationIssue("missing", ["query", "media_type"], "Field required", WireValue.Null));
        }
        else if (FieldRules.TryLiteral(new WireString(rawType), ["query", "media_type"], ProcessingMediaScopes.All, issues, out var parsedType))
        {
            mediaType = parsedType;
        }

        var watchedFolder = WireStrings.Slice(request.Query("watched_folder") ?? string.Empty, 4000);
        var outputFolder = WireStrings.Slice(request.Query("output_folder") ?? string.Empty, 4000);
        var removesOriginals = true;
        if (request.Query("remove_original_after_success") is { } rawRemove)
        {
            if (FieldRules.TryLiteral(new WireString(rawRemove.ToLowerInvariant()), ["query", "remove_original_after_success"], ["true", "false"], issues, out var parsedRemove))
            {
                removesOriginals = parsedRemove == "true";
            }
        }

        var linkedConnectionIds = LinkedConnectionIds(request);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var managers = await _managerSetupCheck
            .CheckAsync(uow, mediaType, watchedFolder, outputFolder, linkedConnectionIds, removesOriginals, cancellationToken: request.Context.RequestAborted)
            .ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("media_type", mediaType).Set("managers", new WireArray(managers.Select(item => (WireValue)item))));
    }

    /// <summary>
    /// <c>GET /api/v1/processing/libraries/{library_id}/folder-chain</c>: one library's folder chain — Weir's own
    /// watched/work/output folders, plus every enabled connection that covers its media type, folded into one plain-
    /// language, read-only view.
    /// </summary>
    public async Task<ApiResult> GetLibraryFolderChainAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("library_id", issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await RequireLibraryAsync(uow, _libraries, id).ConfigureAwait(false);
        var chain = await _folderChainCheck
            .ReadSavedAsync(uow, library, request.Context.RequestAborted)
            .ConfigureAwait(false);
        return ApiRoutes.Ok(chain);
    }

    public async Task<ApiResult> PostLibraryAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var body = ProcessingLibraryMapping.ReadLibraryBody(model);
        model.Finish(ExtraFields.Forbid);
        if (!issues.Any)
        {
            ProcessingLibraryMapping.ValidateDetectionWindows(body, issues);
        }

        issues.ThrowIfAny();
        body = ProcessingLibraryMapping.WithWindowsInUtc(body);

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        ProcessingLibraryRecord row;
        try
        {
            row = await _libraries.CreateAsync(uow, body, request.Options.WeirHome).ConfigureAwait(false);
        }
        catch (ProcessingLibraryException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await ProcessingLibraryMapping.RefuseUnsupportedRejectAsync(uow, row, _libraries, _connections, _rejectSupport).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        _scanSettingsChanges.Record();
        return new JsonApiResult(StatusCodes.Status201Created, await LibraryOutAsync(request, uow, row).ConfigureAwait(false));
    }

    public async Task<ApiResult> PutLibraryAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var pathIssues = new ValidationIssues();
        var id = request.PathInt("library_id", pathIssues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var body = ProcessingLibraryMapping.ReadLibraryBody(model);
        model.Finish(ExtraFields.Forbid);
        pathIssues.ThrowIfAny();
        if (!issues.Any)
        {
            ProcessingLibraryMapping.ValidateDetectionWindows(body, issues);
        }

        issues.ThrowIfAny();
        body = ProcessingLibraryMapping.WithWindowsInUtc(body);

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var existing = await RequireLibraryAsync(uow, _libraries, id).ConfigureAwait(false);
        ProcessingLibraryRecord updated;
        try
        {
            updated = await _libraries.UpdateAsync(uow, existing, body, request.Options.WeirHome).ConfigureAwait(false);
        }
        catch (ProcessingLibraryException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await ProcessingLibraryMapping.RefuseUnsupportedRejectAsync(uow, updated, _libraries, _connections, _rejectSupport).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        _scanSettingsChanges.Record();
        return ApiRoutes.Ok(await LibraryOutAsync(request, uow, updated).ConfigureAwait(false));
    }

    public async Task<ApiResult> DeleteLibraryAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("library_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await RequireLibraryAsync(uow, _libraries, id).ConfigureAwait(false);
        try
        {
            await _libraries.DeleteAsync(uow, row).ConfigureAwait(false);
        }
        catch (ProcessingLibraryException exception)
        {
            throw new ApiException(StatusCodes.Status409Conflict, exception.Message);
        }

        // #505: a deleted library takes its library-mode settings and scan history with it (both live on
        // jobs rows, not a foreign-keyed table — see docs/archive/server-port-notes.md, "Library mode").
        await _librarySettings.DeleteAllForLibraryAsync(uow, row.Id).ConfigureAwait(false);

        await request.CommitAsync().ConfigureAwait(false);
        _scanSettingsChanges.Record();
        return new CustomApiResult(context =>
        {
            ApiResponses.NoContentJson(context);
            return Task.CompletedTask;
        });
    }

    public async Task<ApiResult> PostReorderAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var orderedIds = model.IntList("library_ids_in_order");
        model.Finish(ExtraFields.Forbid);
        if (orderedIds.Count == 0)
        {
            issues.Add(new ValidationIssue("too_short", ["body", "library_ids_in_order"], "List should have at least 1 item after validation, not 0", new WireArray()));
        }

        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        List<ProcessingLibraryRecord> rows;
        try
        {
            rows = await _libraries.ReorderAsync(uow, orderedIds).ConfigureAwait(false);
        }
        catch (ProcessingLibraryException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        var items = new List<WireValue>();
        foreach (var row in rows)
        {
            items.Add(await LibraryOutAsync(request, uow, row).ConfigureAwait(false));
        }

        return ApiRoutes.Ok(new WireArray(items));
    }
}
