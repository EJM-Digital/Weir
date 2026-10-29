using Microsoft.AspNetCore.Routing;

namespace Weir.Api.Endpoints;

/// <summary>
/// Suite-wide settings, configuration bundles and snapshots, logs, metrics, updates and operational
/// history, the global pause, the local directory browser and the media-tools report.
/// </summary>
public static class SuiteEndpoints
{
    public static IEndpointRouteBuilder MapSuiteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSuiteConfigurationEndpoints();
        endpoints.MapSuiteDiagnosticsEndpoints();
        endpoints.MapSuiteUpdateEndpoints();
        endpoints.MapSuiteNetworkAccessEndpoints();
        endpoints.MapSuiteHomeNetworkSignInEndpoints();
        endpoints.MapSuiteOperationalHistoryEndpoints();
        endpoints.MapSuitePauseEndpoints();
        return endpoints;
    }
}
