using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Infrastructure.SystemReadings;

namespace Weir.Api.Endpoints;

/// <summary><c>GET /api/v1/system/stats</c>: the machine's load, Weir's share of it and the drives, with the last ten minutes of it.</summary>
public static class SystemStatsEndpoints
{
    public static IEndpointRouteBuilder MapSystemStatsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<SystemStatsEndpointHandlers>();
        endpoints.MapV1("GET", "/system/stats", handlers.GetStatsAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="SystemStatsEndpoints"/>.</summary>
internal sealed class SystemStatsEndpointHandlers
{
    private readonly SystemStatsStore _store;

    public SystemStatsEndpointHandlers(SystemStatsStore store) => _store = store;

    public async Task<ApiResult> GetStatsAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(SystemStatsWire.Snapshot(_store.Snapshot()));
    }
}
