using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Validation;
using Weir.Infrastructure.Processing.DirectPlay;

namespace Weir.Api.Endpoints;

/// <summary>Choosing the devices the Direct Play badge answers for (#467).</summary>
public static class ProcessingDirectPlayEndpoints
{
    public static IEndpointRouteBuilder MapProcessingDirectPlayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingDirectPlayEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/direct-play/devices", handlers.GetDevicesAsync);
        endpoints.MapV1("PUT", "/processing/direct-play/devices", handlers.PutDevicesAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingDirectPlayEndpoints"/>, constructor-injected with the store it needs.</summary>
internal sealed class ProcessingDirectPlayEndpointHandlers
{
    private readonly DirectPlayService _directPlay;

    public ProcessingDirectPlayEndpointHandlers(DirectPlayService directPlay)
    {
        _directPlay = directPlay;
    }

    private async Task<WireObject> BuildOutAsync(ApiRequest request)
    {
        var uow = await request.DbAsync().ConfigureAwait(false);
        var known = DeviceProfileLoader.Load(request.Options.WeirHome);
        var chosen = new HashSet<string>(await _directPlay.SelectedDeviceIdsAsync(uow).ConfigureAwait(false), StringComparer.Ordinal);
        var devices = known.Select(p => (WireValue)new WireObject()
            .Set("id", p.Id)
            .Set("name", p.Name)
            .Set("source", p.Source)
            .Set("note", p.Note)
            .Set("selected", chosen.Contains(p.Id)));
        return new WireObject()
            .Set("devices", new WireArray(devices))
            .Set("customised", DirectPlayService.IsCustomised(request.Options.WeirHome));
    }

    public async Task<ApiResult> GetDevicesAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(await BuildOutAsync(request).ConfigureAwait(false));
    }

    public async Task<ApiResult> PutDevicesAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var selected = model.StrList("selected", []);
        model.Finish(ExtraFields.Ignore);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var known = DeviceProfileLoader.Load(request.Options.WeirHome);
        await _directPlay.SaveSelectedDeviceIdsAsync(uow, known, selected).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(await BuildOutAsync(request).ConfigureAwait(false));
    }
}
