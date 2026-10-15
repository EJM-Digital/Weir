using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Updates;
using Weir.Core.Validation;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Runtime;

namespace Weir.Api.Endpoints;

/// <summary>Update status, update settings, and the check, download and apply steps a person asks the tray for.</summary>
public static class SuiteUpdateEndpoints
{
    public static IEndpointRouteBuilder MapSuiteUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SuiteUpdateEndpointHandlers>();
        endpoints.MapV1("GET", "/suite/update-status", handlers.GetUpdateStatusAsync);
        endpoints.MapV1("GET", "/suite/update-settings", handlers.GetUpdateSettingsAsync);
        endpoints.MapV1("PUT", "/suite/update-settings", handlers.PutUpdateSettingsAsync);
        endpoints.MapV1("GET", "/suite/update-state", handlers.GetUpdateStateAsync);
        endpoints.MapV1("POST", "/suite/check-update", handlers.PostCheckUpdateAsync);
        endpoints.MapV1("POST", "/suite/download-update", handlers.PostDownloadUpdateAsync);
        endpoints.MapV1("POST", "/suite/apply-update", handlers.PostApplyUpdateAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SuiteUpdateEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class SuiteUpdateEndpointHandlers
{
    private readonly UpdateStatusReader _status;
    private readonly UpdateFiles _files;

    public SuiteUpdateEndpointHandlers(UpdateStatusReader status, UpdateFiles files)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<ApiResult> GetUpdateStatusAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(await _status.ReadAsync(request.Context.RequestAborted).ConfigureAwait(false));
    }

    public async Task<ApiResult> GetUpdateSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var logger = request.LoggerFactory.CreateLogger("weir.platform.suite_settings.update_service");
        return ApiRoutes.Ok(_files.ReadSettings(path => logger.LogWarning(
            "Update settings at {Path} could not be read. Falling back to notify-only so a damaged file cannot install an update the operator did not choose.",
            path)));
    }

    public async Task<ApiResult> PutUpdateSettingsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var mode = model.Literal("mode", UpdateStatus.Modes);
        var checkOnStartup = model.Bool("check_on_startup", defaultValue: true);
        var interval = model.Number("check_interval_minutes", 60, required: false, ge: 1, le: 10080);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        return ApiRoutes.Ok(_files.WriteSettings(mode, checkOnStartup, interval));
    }

    public async Task<ApiResult> GetUpdateStateAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(_files.ReadState());
    }

    public Task<ApiResult> PostCheckUpdateAsync(ApiRequest request) =>
        AskTrayAsync(request, UpdateStatus.StateChecking, _files.WriteCheckFlag);

    public Task<ApiResult> PostDownloadUpdateAsync(ApiRequest request) =>
        AskTrayAsync(request, UpdateStatus.StateDownloading, _files.WriteDownloadFlag);

    /// <summary>
    /// Asks the tray for <paramref name="step"/> by writing its flag. A step already under way is not asked for again, and a
    /// step that cannot run now, because another is under way or the update is downloaded, is refused with the reason.
    /// </summary>
    private async Task<ApiResult> AskTrayAsync(ApiRequest request, string step, Action ask)
    {
        await RequireConfirmedAdminAsync(request).ConfigureAwait(false);
        RequireTray();
        var state = _files.ReadState();
        if (state.Get("downloaded")?.IsTruthy ?? false)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "An update is already downloaded. Restart Weir to apply it.");
        }

        var current = (state.Get("state") as WireString)?.Value;
        if (current == step)
        {
            return ApiRoutes.Ok(state);
        }

        if (current is UpdateStatus.StateChecking or UpdateStatus.StateDownloading)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                current == UpdateStatus.StateChecking ? "Weir is still checking for updates." : "Weir is downloading an update right now.");
        }

        ask();
        return ApiRoutes.Ok(UpdateStatus.WithStep(state, step).Set("tray_running", true));
    }

    public async Task<ApiResult> PostApplyUpdateAsync(ApiRequest request)
    {
        await RequireConfirmedAdminAsync(request).ConfigureAwait(false);
        RequireTray();
        var state = _files.ReadState();
        if (!(state.Get("downloaded")?.IsTruthy ?? false))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "No downloaded update is pending.");
        }

        _files.WriteApplyFlag();
        return ApiRoutes.Ok(state);
    }

    /// <summary>A flag is answered only by a tray: a Docker or source install has none, and one that quit or died answers nothing.</summary>
    private void RequireTray()
    {
        if (!_files.TrayIsRunning())
        {
            throw new ApiException(StatusCodes.Status409Conflict, "The Weir tray isn't running, so Weir can't update itself from here.");
        }
    }

    /// <summary>An update step is for an administrator, and carries the confirmation token and nothing else.</summary>
    private static async Task RequireConfirmedAdminAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
    }
}
