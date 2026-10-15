using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Core.Validation;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Api.Endpoints;

/// <summary>
/// Bare download-client connections — <c>/api/v1/download-clients/connections</c>: an optional, outbound-only
/// connection to SABnzbd, NZBGet, qBittorrent, Deluge or Transmission, used only to read its configuration and
/// suggest a library's watched folder. Weir never controls a download client through this, and a typed folder in
/// the library editor always remains allowed — nothing here is ever applied automatically.
/// </summary>
public static class DownloadClientConnectionsEndpoints
{
    public static IEndpointRouteBuilder MapDownloadClientConnectionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<DownloadClientConnectionsEndpointHandlers>();
        endpoints.MapV1("GET", "/download-clients/connections", handlers.ListConnectionsAsync);
        endpoints.MapV1("POST", "/download-clients/connections", handlers.CreateConnectionAsync);
        endpoints.MapV1("GET", "/download-clients/suggestions", handlers.GetSuggestionsAsync);
        endpoints.MapV1("GET", "/download-clients/connections/{connection_id}", handlers.GetConnectionAsync);
        endpoints.MapV1("PUT", "/download-clients/connections/{connection_id}", handlers.UpdateConnectionAsync);
        endpoints.MapV1("DELETE", "/download-clients/connections/{connection_id}", handlers.DeleteConnectionAsync);
        endpoints.MapV1("POST", "/download-clients/connections/{connection_id}/test", handlers.PostConnectionTestAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="DownloadClientConnectionsEndpoints"/>, constructor-injected with the stores and services they need.</summary>
internal sealed class DownloadClientConnectionsEndpointHandlers
{
    internal const string NoSuchConnection = "That download client connection does not exist.";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly DownloadClientConnectionStore _store;
    private readonly DownloadClientConnectionService _connections;
    private readonly DownloadClientSuggestions _suggestions;
    private readonly IDownloadClientPorts _ports;

    public DownloadClientConnectionsEndpointHandlers(
        DownloadClientConnectionStore store, DownloadClientConnectionService connections, DownloadClientSuggestions suggestions, IDownloadClientPorts ports)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _suggestions = suggestions ?? throw new ArgumentNullException(nameof(suggestions));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    public async Task<ApiResult> ListConnectionsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var rows = await _store.ListAsync(uow).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireArray(rows.Select(row => (WireValue)row.ToOut())));
    }

    /// <summary>A <c>str</c> field with a default: absent means the default, present must be a string.</summary>
    private static string StrWithDefault(BodyModel model, WireValue? body, string name, string defaultValue, int? maxLength) =>
        body is WireObject dict && dict.ContainsKey(name) ? model.Str(name, maxLength: maxLength) : defaultValue;

    public async Task<ApiResult> CreateConnectionAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var kind = model.Literal("kind", DownloadClientKinds.All);
        MediaManagerConnectionsEndpoints.AcceptIgnoredName(model);
        var enabled = model.Bool("enabled", defaultValue: true);
        var baseUrl = StrWithDefault(model, body, "base_url", string.Empty, 2000);
        var username = StrWithDefault(model, body, "username", string.Empty, 200);
        var password = StrWithDefault(model, body, "password", string.Empty, 2000);
        var apiKey = StrWithDefault(model, body, "api_key", string.Empty, 2000);
        var nickname = MediaManagerConnectionsEndpoints.OptionalNickname(model);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        MediaManagerConnectionsEndpoints.VerifyCsrf(request, csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        long id;
        try
        {
            id = await _connections.CreateAsync(
                uow, kind, baseUrl, username.Length > 0 ? username : null, password.Length > 0 ? password : null, apiKey.Length > 0 ? apiKey : null, enabled, nickname)
                .ConfigureAwait(false);
        }
        catch (DownloadClientConnectionException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        var row = await RequireConnectionAsync(uow, id).ConfigureAwait(false);
        return new JsonApiResult(StatusCodes.Status201Created, row.ToOut());
    }

    public async Task<ApiResult> GetConnectionAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        issues.ThrowIfAny();
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok((await RequireConnectionAsync(uow, connectionId).ConfigureAwait(false)).ToOut());
    }

    public async Task<ApiResult> UpdateConnectionAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        MediaManagerConnectionsEndpoints.AcceptIgnoredName(model);
        var enabled = model.OptionalBool("enabled");
        var baseUrl = model.OptionalStr("base_url", maxLength: 2000);
        var username = model.OptionalStr("username", maxLength: 200);
        var password = model.OptionalStr("password", maxLength: 2000);
        var apiKey = model.OptionalStr("api_key", maxLength: 2000);
        var nickname = MediaManagerConnectionsEndpoints.OptionalNickname(model);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        MediaManagerConnectionsEndpoints.VerifyCsrf(request, csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await RequireConnectionAsync(uow, connectionId).ConfigureAwait(false);
        try
        {
            await _connections.UpdateAsync(uow, row, baseUrl, username, password, apiKey, enabled, nickname).ConfigureAwait(false);
        }
        catch (DownloadClientConnectionException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok((await RequireConnectionAsync(uow, connectionId).ConfigureAwait(false)).ToOut());
    }

    public async Task<ApiResult> DeleteConnectionAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        MediaManagerConnectionsEndpoints.VerifyCsrf(request, csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        await RequireConnectionAsync(uow, connectionId).ConfigureAwait(false);
        await _store.DeleteAsync(uow, connectionId).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return new CustomApiResult(context =>
        {
            ApiResponses.NoContentJson(context);
            return Task.CompletedTask;
        });
    }

    public async Task<ApiResult> PostConnectionTestAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        MediaManagerConnectionsEndpoints.VerifyCsrf(request, csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await RequireConnectionAsync(uow, connectionId).ConfigureAwait(false);
        var checkedAt = Timestamp.UtcNow(request.Time);

        bool ok;
        string detail;
        var connection = _connections.ConnectionFromRow(row);
        var port = _ports.PortForKind(row.Kind);
        if (connection is null)
        {
            (ok, detail) = (false, "Add the address where this app can be reached, then test again.");
        }
        else if (port is null)
        {
            (ok, detail) = (false, $"Weir does not know how to talk to {row.Label}.");
        }
        else
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Context.RequestAborted, timeout.Token);
            (ok, detail) = await port.TestAsync(connection, linked.Token).ConfigureAwait(false);
        }

        // The probe can take seconds and the connection may be removed meanwhile, so the write is conditional.
        if (await _store.RecordTestResultAsync(uow, connectionId, ok, checkedAt, detail).ConfigureAwait(false) == 0)
        {
            await uow.RollbackAsync().ConfigureAwait(false);
            throw new ApiException(StatusCodes.Status404NotFound, "That download client connection was removed while its connection test was running.");
        }

        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("connection_id", row.Id)
            .Set("ok", ok)
            .Set("detail", detail)
            .Set("checked_at", checkedAt.ToWireText()));
    }

    /// <summary>
    /// <c>GET /api/v1/download-clients/suggestions?media_type=movie|tv</c>: one entry per enabled connection with the
    /// folder it saves to, to offer as a workflow's watched folder. An entry gives no verdict on whether that folder
    /// suits a workflow; the workflow's folder chain says so. <c>media_type</c> is accepted for route consistency with
    /// the manager setup route but not used to filter: a download client's completed folder is what it is regardless of
    /// media type, since none of the five dialects can reliably say which category is "the TV one".
    /// </summary>
    public async Task<ApiResult> GetSuggestionsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        if (request.Query("media_type") is { } rawType)
        {
            FieldRules.TryLiteral(new WireString(rawType), ["query", "media_type"], ProcessingMediaScopes.All, issues, out _);
        }
        else
        {
            issues.Add(new ValidationIssue("missing", ["query", "media_type"], "Field required", WireValue.Null));
        }

        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var suggestions = await _suggestions.SuggestAsync(uow, request.Context.RequestAborted).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireArray(suggestions.Select(item => (WireValue)item)));
    }

    /// <summary>The <c>connection_id</c> route value as an integer of at least 1; 0 with a validation issue otherwise.</summary>
    private static long ConnectionId(ApiRequest request, ValidationIssues issues)
    {
        var raw = request.RouteValue("connection_id") ?? string.Empty;
        return FieldRules.TryInt(new WireString(raw), ["path", "connection_id"], 1, null, issues, out var value)
            ? value > long.MaxValue ? long.MaxValue : (long)value
            : 0;
    }

    private async Task<DownloadClientConnectionRecord> RequireConnectionAsync(Infrastructure.Sqlite.UnitOfWork uow, long connectionId) =>
        await _store.GetAsync(uow, connectionId).ConfigureAwait(false)
        ?? throw new ApiException(StatusCodes.Status404NotFound, NoSuchConnection);
}
