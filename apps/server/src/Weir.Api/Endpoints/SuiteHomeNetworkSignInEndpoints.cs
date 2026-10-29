using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Net;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Settings;

namespace Weir.Api.Endpoints;

/// <summary>
/// "Skip sign-in for devices on your home network", shown and changed from System › Security
/// (docs/security-hardening.md#skip-sign-in-on-the-home-network).
/// </summary>
public static class SuiteHomeNetworkSignInEndpoints
{
    public static IEndpointRouteBuilder MapSuiteHomeNetworkSignInEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SuiteHomeNetworkSignInEndpointHandlers>();
        endpoints.MapV1("GET", "/suite/home-network-sign-in", handlers.GetAsync);
        endpoints.MapV1("PUT", "/suite/home-network-sign-in", handlers.PutAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SuiteHomeNetworkSignInEndpoints"/>.</summary>
internal sealed class SuiteHomeNetworkSignInEndpointHandlers
{
    private readonly HomeNetworkSignInStore _store;
    private readonly ActivityStore _activity;

    public SuiteHomeNetworkSignInEndpointHandlers(HomeNetworkSignInStore store, ActivityStore activity)
    {
        _store = store;
        _activity = activity;
    }

    public async Task<ApiResult> GetAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(Out(await _store.GetAsync(uow).ConfigureAwait(false)));
    }

    /// <summary>
    /// Saves the choice. It asks for the current password even of a visitor who skipped sign-in, and the Activity line is written
    /// only when the value changes, not when only the networks do.
    /// </summary>
    public async Task<ApiResult> PutAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var currentPassword = model.Str("current_password", minLength: 1, maxLength: 512);
        var enabled = model.Bool("enabled", defaultValue: false, required: true);
        var extraNetworks = model.StrList("extra_networks", []);
        var allowList = model.StrList("allow_list", []);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        await request.RequireCurrentPasswordAsync(currentPassword).ConfigureAwait(false);
        HomeNetworkSettings settings;
        try
        {
            settings = new HomeNetworkSettings(enabled, HomeNetwork.CleanEntries(extraNetworks), HomeNetwork.CleanEntries(allowList));
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var before = await _store.GetAsync(uow).ConfigureAwait(false);
        await _store.SaveAsync(uow, settings).ConfigureAwait(false);
        if (before.Enabled != settings.Enabled)
        {
            await _activity.RecordAsync(
                uow, ActivityEventTypes.SystemHomeNetworkSignInChanged, "system", HomeNetworkWords.ActivityTitle, HomeNetworkWords.ActivityDetail(settings.Enabled)).ConfigureAwait(false);
        }

        return ApiRoutes.Ok(Out(settings));
    }

    private static WireObject Out(HomeNetworkSettings settings) => new WireObject()
        .Set("enabled", settings.Enabled)
        .Set("extra_networks", new WireArray(settings.ExtraNetworks.Select(entry => (WireValue)new WireString(entry))))
        .Set("allow_list", new WireArray(settings.AllowList.Select(entry => (WireValue)new WireString(entry))))
        .Set("warning", HomeNetworkWords.Warning(settings, AllowedHostPolicy.Enforced));
}
