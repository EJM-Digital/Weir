using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Activity;
using Weir.Core.Auth;
using Weir.Core.Configuration;
using Weir.Core.Validation;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Runtime;

namespace Weir.Api.Endpoints;

/// <summary>
/// Who can reach Weir over the network, shown and changed from System › About
/// (docs/security-hardening.md#windows-firewall).
/// </summary>
public static class SuiteNetworkAccessEndpoints
{
    public static IEndpointRouteBuilder MapSuiteNetworkAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SuiteNetworkAccessEndpointHandlers>();
        endpoints.MapV1("GET", "/suite/network-access", handlers.GetNetworkAccessAsync);
        endpoints.MapV1("PUT", "/suite/network-access", handlers.PutNetworkAccessAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SuiteNetworkAccessEndpoints"/>.</summary>
internal sealed class SuiteNetworkAccessEndpointHandlers
{
    private readonly INetworkAccess _access;
    private readonly MachineIdentity _machine;
    private readonly ActivityStore _activity;

    public SuiteNetworkAccessEndpointHandlers(INetworkAccess access, MachineIdentity machine, ActivityStore activity)
    {
        _access = access;
        _machine = machine;
        _activity = activity;
    }

    public async Task<ApiResult> GetNetworkAccessAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(NetworkAccessStatusWire.From(_access.Read(), _machine.Name, _access.NotChangeableReason));
    }

    public async Task<ApiResult> PutNetworkAccessAsync(ApiRequest request)
    {
        var admin = await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var scope = NetworkAccessStatusWire.ParseScope(model.Literal("scope", NetworkAccessStatusWire.Scopes));
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        if (_access.NotChangeableReason is { } reason)
        {
            throw new ApiException(StatusCodes.Status409Conflict, reason);
        }

        _access.Choose(scope);
        request.LoggerFactory.CreateLogger("weir.platform.network_access").LogInformation(
            "network access: {Scope} chosen (user_id={UserId})",
            NetworkAccessStatusWire.WireScope(scope),
            admin.User.Id);
        var uow = await request.DbAsync().ConfigureAwait(false);
        await _activity.RecordAsync(
            uow,
            ActivityEventTypes.SystemNetworkAccessChanged,
            "system",
            "Network access changed",
            $"{NetworkAccessStatusWire.Choice(scope)} Changed by {admin.User.Username}.").ConfigureAwait(false);
        return ApiRoutes.Ok(NetworkAccessStatusWire.From(_access.Read(), _machine.Name, _access.NotChangeableReason));
    }
}
