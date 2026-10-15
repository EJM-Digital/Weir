using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Time;
using Weir.Core.Validation;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using static Weir.Api.Endpoints.EndpointLookups;

namespace Weir.Api.Endpoints;

/// <summary>
/// Media manager connections and capabilities: create/list/edit/delete a connection, the
/// webhook secret it authenticates with, and the connection test also used by the heartbeat
/// (<see cref="ManagerHealthProbe"/>).
/// </summary>
public static class MediaManagerConnectionsEndpoints
{
    internal const string InvalidCsrf = "Invalid or expired CSRF token.";

    private const int ConnectionNameMaxLength = 200;

    public static IEndpointRouteBuilder MapMediaManagerConnectionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<MediaManagerConnectionsEndpointHandlers>();
        endpoints.MapV1("GET", "/media-managers/connections", handlers.ListConnectionsAsync);
        endpoints.MapV1("POST", "/media-managers/connections", handlers.CreateConnectionAsync);
        endpoints.MapV1("GET", "/media-managers/capabilities", handlers.GetCapabilitiesAsync);
        endpoints.MapV1("GET", "/media-managers/connections/{connection_id}", handlers.GetConnectionAsync);
        endpoints.MapV1("PUT", "/media-managers/connections/{connection_id}", handlers.UpdateConnectionAsync);
        endpoints.MapV1("DELETE", "/media-managers/connections/{connection_id}", handlers.DeleteConnectionAsync);
        endpoints.MapV1("POST", "/media-managers/connections/{connection_id}/unlink-workflows", handlers.PostUnlinkWorkflowsAsync);
        endpoints.MapV1("POST", "/media-managers/connections/{connection_id}/give-up-reports", handlers.PostGiveUpReportsAsync);
        endpoints.MapV1("POST", "/media-managers/connections/{connection_id}/webhook-secret", handlers.PostWebhookSecretAsync);
        endpoints.MapV1("PUT", "/media-managers/connections/{connection_id}/lanes/{lane}", handlers.PutLaneAsync);
        endpoints.MapV1("POST", "/media-managers/connections/{connection_id}/test", handlers.PostConnectionTestAsync);
        endpoints.MapV1("GET", "/media-managers/connections/{connection_id}/folder-chain", handlers.GetConnectionFolderChainAsync);
        return endpoints;
    }

    /// <summary>
    /// Deluno still sends a <c>name</c> when it creates its connection to Weir. Connections are named after their
    /// kind and address now (#826), so the field is accepted and dropped rather than refused as an unknown one.
    /// Shared with <see cref="DownloadClientConnectionsEndpoints"/>.
    /// </summary>
    internal static void AcceptIgnoredName(BodyModel model) => model.OptionalStr("name", maxLength: ConnectionNameMaxLength);

    /// <summary>The optional <c>nickname</c> a connection is shown with. Shared with <see cref="DownloadClientConnectionsEndpoints"/>.</summary>
    internal static string? OptionalNickname(BodyModel model) => model.OptionalStr("nickname", maxLength: ConnectionNicknames.MaxLength);

    /// <summary>Browser origin, session secret, then a session-bound CSRF token; 400 on a bad token. Shared
    /// with <see cref="MediaManagerReconciliationEndpoints"/> and <see cref="DownloadClientConnectionsEndpoints"/>,
    /// which guard their own routes the same way.</summary>
    internal static void VerifyCsrf(ApiRequest request, string? token)
    {
        request.ValidateBrowserPostOrigin();
        var secret = request.RequireSessionSecret();
        if (!request.VerifyCsrf(secret, token, allowAnonymous: false))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, InvalidCsrf);
        }
    }
}

/// <summary>Handlers for <see cref="MediaManagerConnectionsEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class MediaManagerConnectionsEndpointHandlers
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly MediaManagerConnectionService _connections;
    private readonly MediaManagerConnectionStore _connectionStore;
    private readonly LibraryFolderChainCheck _folderChainCheck;
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly ManagerWorkflowSync _workflowSync;
    private readonly MediaManagerConnectionDependents _dependents;
    private readonly ScanSettingsChanges _scanSettingsChanges;

    public MediaManagerConnectionsEndpointHandlers(
        MediaManagerConnectionService connections,
        MediaManagerConnectionStore connectionStore,
        LibraryFolderChainCheck folderChainCheck,
        IManagerHttpHandlerFactory handlers,
        ManagerWorkflowSync workflowSync,
        MediaManagerConnectionDependents dependents,
        ScanSettingsChanges scanSettingsChanges)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _connectionStore = connectionStore ?? throw new ArgumentNullException(nameof(connectionStore));
        _folderChainCheck = folderChainCheck ?? throw new ArgumentNullException(nameof(folderChainCheck));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _workflowSync = workflowSync ?? throw new ArgumentNullException(nameof(workflowSync));
        _dependents = dependents ?? throw new ArgumentNullException(nameof(dependents));
        _scanSettingsChanges = scanSettingsChanges ?? throw new ArgumentNullException(nameof(scanSettingsChanges));
    }

    public async Task<ApiResult> ListConnectionsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var rows = await _connectionStore.ListAsync(uow).ConfigureAwait(false);
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
        var kind = model.Literal("kind", MediaManagerKinds.All);
        MediaManagerConnectionsEndpoints.AcceptIgnoredName(model);
        var enabled = model.Bool("enabled", defaultValue: true);
        var baseUrl = StrWithDefault(model, body, "base_url", string.Empty, 2000);
        var apiKey = StrWithDefault(model, body, "api_key", string.Empty, 2000);
        var downloadedScanEnabled = model.Bool("downloaded_scan_enabled", defaultValue: false);
        var nickname = MediaManagerConnectionsEndpoints.OptionalNickname(model);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        MediaManagerConnectionsEndpoints.VerifyCsrf(request, csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        long id;
        try
        {
            id = await _connections
                .CreateAsync(uow, kind, baseUrl, apiKey.Length > 0 ? apiKey : null, enabled, downloadedScanEnabled, nickname).ConfigureAwait(false);
        }
        catch (MediaManagerConnectionException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        _workflowSync.RequestSync();
        var row = await RequireConnectionAsync(uow, _connectionStore, id).ConfigureAwait(false);
        return new JsonApiResult(StatusCodes.Status201Created, row.ToOut());
    }

    public async Task<ApiResult> GetCapabilitiesAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var output = new WireArray();
        foreach (var described in await _connections.DescribeConnectionsAsync(uow, request.Context.RequestAborted).ConfigureAwait(false))
        {
            var capabilities = described.Capabilities;
            output.Items.Add(new WireObject()
                .Set("connection_id", described.Connection.ConnectionId ?? 0)
                .Set("kind", described.Connection.Kind)
                .Set("name", described.Connection.Name)
                .Set("label", described.Connection.Label)
                .Set("media_scopes", new WireArray(capabilities.Scopes.Order(StringComparer.Ordinal).Select(scope => (WireValue)new WireString(scope))))
                .Set("reports_import_queue", capabilities.ReportsQueue)
                .Set("reports_library_truth", capabilities.ReportsLibraryTruth)
                .Set("reachable", described.Status == SignalStatus.Reported)
                .Set("library_roots", new WireArray(described.LibraryRoots.Select(root => (WireValue)new WireString(root))))
                .Set("summary", capabilities.Summary)
                .Set("detail", described.Detail));
        }

        return ApiRoutes.Ok(output);
    }

    public async Task<ApiResult> GetConnectionAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        issues.ThrowIfAny();
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok((await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false)).ToOut());
    }

    /// <summary>
    /// <c>GET /api/v1/media-managers/connections/{connection_id}/folder-chain</c>: the same per-library
    /// folder-chain check <c>ProcessingLibraryEndpoints.GetLibraryFolderChainAsync</c> exposes per library, run for
    /// every library linked to this connection — "which of this connection's libraries are fully chained".
    /// </summary>
    public async Task<ApiResult> GetConnectionFolderChainAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        var chain = await _folderChainCheck
            .CheckForConnectionAsync(uow, connectionId, request.Context.RequestAborted)
            .ConfigureAwait(false);
        return ApiRoutes.Ok(new WireArray(chain.Select(item => (WireValue)item)));
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
        var apiKey = model.OptionalStr("api_key", maxLength: 2000);
        var downloadedScanEnabled = model.OptionalBool("downloaded_scan_enabled");
        var nickname = MediaManagerConnectionsEndpoints.OptionalNickname(model);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        MediaManagerConnectionsEndpoints.VerifyCsrf(request, csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        try
        {
            await _connections.UpdateAsync(uow, row, baseUrl, apiKey, enabled, downloadedScanEnabled, nickname).ConfigureAwait(false);
        }
        catch (MediaManagerConnectionException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        await request.CommitAsync().ConfigureAwait(false);
        if (baseUrl is not null || apiKey is not null || enabled is not null)
        {
            _workflowSync.RequestSync();
        }

        return ApiRoutes.Ok((await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false)).ToOut());
    }

    public async Task<ApiResult> DeleteConnectionAsync(ApiRequest request)
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
        // The write lock comes first, so a hand-off that arrives while this decides cannot slip in after the check.
        await uow.BeginImmediateAsync().ConfigureAwait(false);
        var row = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        var blockers = await _dependents.BlockersAsync(uow, row).ConfigureAwait(false);
        if (blockers.Any)
        {
            // Checking a hand-off's state refreshes its record, so that is kept even though nothing is removed.
            await request.CommitAsync().ConfigureAwait(false);
            throw new ApiException(StatusCodes.Status409Conflict, blockers.RemovalRefusal(row.Label))
            {
                Code = ConnectionRemovalBlockers.InUseCode,
                Details = blockers.ToWire(),
            };
        }

        await _connectionStore.DeleteAsync(uow, connectionId).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return new CustomApiResult(context =>
        {
            ApiResponses.NoContentJson(context);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// <c>POST /media-managers/connections/{connection_id}/unlink-workflows</c>: the first of the two ways to let go of the workflows
    /// that keep a connection from being removed. The workflows stay, with their settings and folders; a workflow no manager feeds
    /// any more is switched off until the person chooses what it does next. Refused while a hand-off from the connection is still
    /// in progress, because Weir would then treat the download as its own to release before the manager has its file.
    /// </summary>
    public async Task<ApiResult> PostUnlinkWorkflowsAsync(ApiRequest request)
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
        await uow.BeginImmediateAsync().ConfigureAwait(false);
        var row = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        var blockers = await _dependents.BlockersAsync(uow, row).ConfigureAwait(false);
        if (blockers.HandoffsInProgress > 0)
        {
            await request.CommitAsync().ConfigureAwait(false);
            throw new ApiException(StatusCodes.Status409Conflict, blockers.UnlinkRefusal(row.Label))
            {
                Code = ConnectionRemovalBlockers.HandoffsInProgressCode,
                Details = blockers.ToWire(),
            };
        }

        var unlinked = await _dependents.UnlinkWorkflowsAsync(uow, row).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        if (unlinked.Count > 0)
        {
            _scanSettingsChanges.Record();
        }

        return ApiRoutes.Ok(new WireObject().Set("workflows", new WireArray(unlinked.Select(workflow => (WireValue)workflow.ToWire()))));
    }

    /// <summary>
    /// <c>POST /media-managers/connections/{connection_id}/give-up-reports</c>: the third way to let go of what keeps a connection from
    /// being removed. Weir stops owing the connection the reports it could not deliver (a manager that never comes back would otherwise
    /// hold it until the hand-offs are pruned), tells nobody, and records in Activity which reports it dropped.
    /// </summary>
    public async Task<ApiResult> PostGiveUpReportsAsync(ApiRequest request)
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
        await uow.BeginImmediateAsync().ConfigureAwait(false);
        var row = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        var dropped = await _dependents.GiveUpOwedReportsAsync(uow, row).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("reports", new WireArray(dropped.Select(report => (WireValue)report.ToWire()))));
    }

    public async Task<ApiResult> PostWebhookSecretAsync(ApiRequest request)
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
        var row = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        var plaintext = await _connections.RotateWebhookSecretAsync(uow, row).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("connection_id", row.Id)
            .Set("webhook_secret", plaintext)
            .Set("webhook_url_path", row.WebhookUrlPath)
            .Set("header_name", "X-Webhook-Secret"));
    }

    /// <summary>
    /// Searching for missing files and upgrades belongs to the media manager, so a connection has no search lanes. An older
    /// client may still save one: it is answered with the connection as it is, and nothing it sends is kept.
    /// </summary>
    private static readonly string[] RetiredLaneSettings =
    [
        "enabled", "max_items_per_run", "retry_delay_minutes", "schedule_enabled", "schedule_days", "schedule_start", "schedule_end",
        "schedule_interval_seconds",
    ];

    public async Task<ApiResult> PutLaneAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var connectionId = ConnectionId(request, issues);
        FieldRules.TryLiteral(new WireString(request.RouteValue("lane") ?? string.Empty), ["path", "lane"], MediaManagerKinds.RetiredSearchLanes, issues, out _);
        var model = new BodyModel(body, issues);
        model.AcceptAndIgnore(RetiredLaneSettings);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        MediaManagerConnectionsEndpoints.VerifyCsrf(request, csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok((await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false)).ToOut());
    }

    /// <summary>The connection test, shared with the heartbeat (<see cref="ManagerHealthProbe"/>).</summary>
    private Task<(bool Ok, string Detail)> ProbeAsync(ApiRequest request, MediaManagerConnectionRecord row, string? apiKey) =>
        ManagerHealthProbe.ProbeAsync(_handlers, row.Label, row.Kind, row.BaseUrl, apiKey, TestTimeout, ConnectionRef.ForManager(row.Id), request.Context.RequestAborted);

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
        var row = await RequireConnectionAsync(uow, _connectionStore, connectionId).ConfigureAwait(false);
        var checkedAt = Timestamp.UtcNow(request.Time);

        bool ok;
        string detail;
        if (WireStrings.Strip(row.BaseUrl).Length == 0)
        {
            (ok, detail) = (false, "Add the address where this app can be reached, then test again.");
        }
        else
        {
            var apiKey = string.IsNullOrEmpty(row.ApiKeyCiphertext) ? null : _connections.Cipher.Decrypt(row.ApiKeyCiphertext);
            (ok, detail) = await ProbeAsync(request, row, apiKey).ConfigureAwait(false);
        }

        // The probe can take seconds and the connection may be removed meanwhile, so the write is conditional.
        if (await _connectionStore.RecordTestResultAsync(uow, connectionId, ok, checkedAt, detail).ConfigureAwait(false) == 0)
        {
            await uow.RollbackAsync().ConfigureAwait(false);
            throw new ApiException(StatusCodes.Status404NotFound, "That media manager connection was removed while its connection test was running.");
        }

        await request.CommitAsync().ConfigureAwait(false);
        if (ok)
        {
            _workflowSync.RequestSync();
        }

        return ApiRoutes.Ok(new WireObject()
            .Set("connection_id", row.Id)
            .Set("ok", ok)
            .Set("detail", detail)
            .Set("checked_at", checkedAt.ToWireText()));
    }
}
