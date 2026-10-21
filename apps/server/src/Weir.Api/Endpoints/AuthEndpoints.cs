using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;

namespace Weir.Api.Endpoints;

/// <summary>Cookie-session auth under <c>/api/v1/auth</c>.</summary>
public static class AuthEndpoints
{
    internal const string ConfirmationExpired = "Your confirmation token expired. Refresh the page and try again.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAuthSessionLifecycleEndpoints();
        endpoints.MapAuthSessionEndpoints();
        endpoints.MapAuthAccountEndpoints();
        return endpoints;
    }

    internal static ILogger Logger(ApiRequest request) => request.LoggerFactory.CreateLogger("weir.platform.auth.router");
}
