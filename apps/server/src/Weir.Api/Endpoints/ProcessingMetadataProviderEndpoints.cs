using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Rules;
using Weir.Core.Validation;
using Weir.Infrastructure.Artwork;

namespace Weir.Api.Endpoints;

/// <summary>
/// The metadata-provider routes. There is nothing left to set: Weir gets posters and original languages from Deluno's metadata
/// service on its own. The routes keep answering, with the shape older clients read, so a client written for the old settings does
/// not break; what a save sends is accepted and ignored.
/// </summary>
public static class ProcessingMetadataProviderEndpoints
{
    public static IEndpointRouteBuilder MapProcessingMetadataProviderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingMetadataProviderEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/metadata-provider", handlers.GetMetadataProviderAsync);
        endpoints.MapV1("PUT", "/processing/metadata-provider", handlers.PutMetadataProviderAsync);
        endpoints.MapV1("POST", "/processing/metadata-provider/test", handlers.PostMetadataProviderTestAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ProcessingMetadataProviderEndpoints"/>.</summary>
internal sealed class ProcessingMetadataProviderEndpointHandlers
{
    private const string ProviderName = "deluno-gateway";
    private const int MaxFieldLength = 500;

    private readonly ArtworkGatewayClient _gateway;

    public ProcessingMetadataProviderEndpointHandlers(ArtworkGatewayClient gateway)
    {
        _gateway = gateway;
    }

    private static WireObject MetadataProviderOut() => new WireObject()
        .Set("provider", ProviderName)
        .Set("base_url", WireNull.Instance)
        .Set("key_configured", false)
        .Set("known_providers", new WireArray([WireValue.Of(ProviderName)]))
        .Set("artwork_enabled", true);

    public async Task<ApiResult> GetMetadataProviderAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(MetadataProviderOut());
    }

    public async Task<ApiResult> PutMetadataProviderAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var csrfToken = await ReadIgnoredBodyAsync(request).ConfigureAwait(false);
        request.RequireConfirmationToken(csrfToken);
        return ApiRoutes.Ok(MetadataProviderOut());
    }

    public async Task<ApiResult> PostMetadataProviderTestAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var csrfToken = await ReadIgnoredBodyAsync(request).ConfigureAwait(false);
        request.RequireConfirmationToken(csrfToken);
        var result = await CheckGatewayAsync(request.Context.RequestAborted).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("status", result.Status).Set("detail", result.Detail));
    }

    /// <summary>Reads the fields the old settings sent, checking their types and ignoring their values. Returns the confirmation token.</summary>
    private static async Task<string> ReadIgnoredBodyAsync(ApiRequest request)
    {
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.OptionalStr("provider", defaultValue: string.Empty, maxLength: MaxFieldLength);
        model.OptionalStr("base_url", defaultValue: string.Empty, maxLength: MaxFieldLength);
        model.OptionalStr("api_key", maxLength: MaxFieldLength);
        model.OptionalBool("artwork_enabled");
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();
        return csrfToken;
    }

    private async Task<LookupResult> CheckGatewayAsync(CancellationToken cancellationToken)
    {
        if (!_gateway.IsConfigured)
        {
            return new LookupResult { Status = LookupResult.StatusNotConfigured, Detail = "Deluno's metadata service is switched off on this server." };
        }

        return await _gateway.CheckHealthAsync(cancellationToken).ConfigureAwait(false) == GatewayStatus.Ok
            ? new LookupResult { Status = LookupResult.StatusMatched, Detail = "Weir reached Deluno's metadata service." }
            : new LookupResult { Status = LookupResult.StatusUnreachable, Detail = "Weir could not reach Deluno's metadata service just now." };
    }
}
