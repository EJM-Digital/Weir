using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;
using static Weir.Api.Endpoints.EndpointLookups;

namespace Weir.Api.Endpoints;

/// <summary>Media-manager library discovery (#554): discover/drift/import against a connection's own
/// libraries, and forgetting where an imported library came from. Backed by
/// <see cref="LibraryDiscoveryService"/>.</summary>
public static class ProcessingLibraryDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapProcessingLibraryDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingLibraryDiscoveryEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/libraries/discover/{connection_id}", handlers.GetDiscoverableLibrariesAsync);
        endpoints.MapV1("POST", "/processing/libraries/discover/{connection_id}/import", handlers.PostImportLibrariesAsync);
        endpoints.MapV1("GET", "/processing/libraries/discover/{connection_id}/drift", handlers.GetLibraryDriftAsync);
        endpoints.MapV1("POST", "/processing/libraries/{library_id}/unlink", handlers.PostLibraryUnlinkAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingLibraryDiscoveryEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingLibraryDiscoveryEndpointHandlers
{
    private readonly LibraryStore _libraries;
    private readonly MediaManagerConnectionStore _connectionStore;
    private readonly OperatorSettingsStore _operatorSettings;
    private readonly SuiteSettingsStore _suiteSettings;
    private readonly ScanWakeups _scanWakeups;
    private readonly LibraryDiscoveryService _discovery;
    private readonly ScanSettingsChanges _scanSettingsChanges;

    public ProcessingLibraryDiscoveryEndpointHandlers(
        LibraryStore libraries,
        MediaManagerConnectionStore connectionStore,
        OperatorSettingsStore operatorSettings,
        SuiteSettingsStore suiteSettings,
        ScanWakeups scanWakeups,
        LibraryDiscoveryService discovery,
        ScanSettingsChanges scanSettingsChanges)
    {
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _connectionStore = connectionStore ?? throw new ArgumentNullException(nameof(connectionStore));
        _operatorSettings = operatorSettings ?? throw new ArgumentNullException(nameof(operatorSettings));
        _suiteSettings = suiteSettings ?? throw new ArgumentNullException(nameof(suiteSettings));
        _scanWakeups = scanWakeups ?? throw new ArgumentNullException(nameof(scanWakeups));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _scanSettingsChanges = scanSettingsChanges ?? throw new ArgumentNullException(nameof(scanSettingsChanges));
    }

    private Task<WireObject> LibraryOutAsync(ApiRequest request, Weir.Infrastructure.Sqlite.UnitOfWork uow, ProcessingLibraryRecord row) =>
        ProcessingLibraryMapping.LibraryOutAsync(request, uow, row, _libraries, _connectionStore, _operatorSettings, _suiteSettings, _scanWakeups);

    private static WireObject DiscoverableLibraryOut(DiscoverableLibrary item) => new WireObject()
        .Set("key", item.Key)
        .Set("name", item.Name)
        .Set("media_type", item.MediaType)
        .Set("root_path", item.RootPath)
        .Set("already_imported", item.AlreadyImported)
        .Set("local_path_problem", item.LocalPathProblem)
        .Set("output_path", item.OutputPath)
        .Set("processes_before_import", item.ProcessesBeforeImport)
        .Set("output_path_problem", item.OutputPathProblem);

    private static WireObject LibraryDriftOut(LibraryDrift item) => new WireObject()
        .Set("kind", item.Kind)
        .Set("library_id", item.LibraryId)
        .Set("library_name", item.LibraryName)
        .Set("manager_value", item.ManagerValue)
        .Set("weir_value", item.WeirValue)
        .Set("detail", item.Detail);

    /// <summary><c>GET /processing/libraries/discover/{connection_id}</c>: what this manager says it looks
    /// after, and whether Weir already has it.</summary>
    public async Task<ApiResult> GetDiscoverableLibrariesAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var connection = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        List<DiscoverableLibrary> found;
        try
        {
            found = await _discovery
                .DiscoverableLibrariesAsync(uow, connection, request.Context.RequestAborted).ConfigureAwait(false);
        }
        catch (ProcessingDiscoveryException exception)
        {
            throw new ApiException(StatusCodes.Status502BadGateway, exception.Message);
        }

        return ApiRoutes.Ok(new WireArray(found.Select(item => (WireValue)DiscoverableLibraryOut(item))));
    }

    /// <summary><c>POST /processing/libraries/discover/{connection_id}/import</c>: create a Processing library per
    /// selected manager library.</summary>
    public async Task<ApiResult> PostImportLibrariesAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var keys = model.StrList("keys", []);
        model.Finish(ExtraFields.Forbid);
        if (keys.Count == 0)
        {
            issues.Add(new ValidationIssue("too_short", ["body", "keys"], "List should have at least 1 item after validation, not 0", new WireArray()));
        }

        issues.ThrowIfAny();

        // Every route in this file refuses a bad token with this exact wording, which clients match on.
        request.RequireConfirmationToken(csrfToken, "Invalid or expired CSRF token.");

        var uow = await request.DbAsync().ConfigureAwait(false);
        var connection = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        List<ProcessingLibraryRecord> created;
        try
        {
            created = await _discovery
                .ImportLibrariesAsync(uow, connection, keys, request.Context.RequestAborted).ConfigureAwait(false);
        }
        catch (ProcessingDiscoveryException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        _scanSettingsChanges.Record();
        var items = new List<WireValue>();
        foreach (var row in created)
        {
            items.Add(await LibraryOutAsync(request, uow, row).ConfigureAwait(false));
        }

        return new JsonApiResult(StatusCodes.Status201Created, new WireArray(items));
    }

    /// <summary><c>GET /processing/libraries/discover/{connection_id}/drift</c>: differences between the manager
    /// and Weir. Reported only — nothing is applied.</summary>
    public async Task<ApiResult> GetLibraryDriftAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var connection = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        List<LibraryDrift> drift;
        try
        {
            drift = await _discovery
                .ResyncDriftAsync(uow, connection, request.Context.RequestAborted).ConfigureAwait(false);
        }
        catch (ProcessingDiscoveryException exception)
        {
            throw new ApiException(StatusCodes.Status502BadGateway, exception.Message);
        }

        return ApiRoutes.Ok(new WireArray(drift.Select(item => (WireValue)LibraryDriftOut(item))));
    }

    /// <summary><c>POST /processing/libraries/{library_id}/unlink</c>: forget where a library came from. The
    /// library itself is untouched.</summary>
    public async Task<ApiResult> PostLibraryUnlinkAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("library_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        // Every route in this file refuses a bad token with this exact wording, which clients match on.
        request.RequireConfirmationToken(csrfToken, "Invalid or expired CSRF token.");

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await RequireLibraryAsync(uow, _libraries, id).ConfigureAwait(false);
        var updated = await _discovery.UnlinkLibraryAsync(uow, row).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(await LibraryOutAsync(request, uow, updated).ConfigureAwait(false));
    }
}
