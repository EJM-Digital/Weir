using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Infrastructure.Scheduling;

namespace Weir.Api.Endpoints;

/// <summary><c>GET /api/v1/system/overview</c> and <c>GET /api/v1/system/tasks</c>: the facts and the scheduled tasks System shows.</summary>
public static class SystemOverviewEndpoints
{
    public static IEndpointRouteBuilder MapSystemOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SystemOverviewEndpointHandlers>();
        endpoints.MapV1("GET", "/system/overview", handlers.GetOverviewAsync);
        endpoints.MapV1("GET", "/system/tasks", handlers.GetTasksAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SystemOverviewEndpoints"/>.</summary>
internal sealed class SystemOverviewEndpointHandlers
{
    private readonly SystemOverviewReader _overview;
    private readonly PeriodicTaskRegistry _tasks;

    public SystemOverviewEndpointHandlers(SystemOverviewReader overview, PeriodicTaskRegistry tasks)
    {
        _overview = overview;
        _tasks = tasks;
    }

    public async Task<ApiResult> GetOverviewAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(await _overview.ReadAsync(uow, request.Context.RequestServices, request.Context.RequestAborted).ConfigureAwait(false));
    }

    public async Task<ApiResult> GetTasksAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(SystemTasksWire.List(_tasks.Snapshot()));
    }
}
