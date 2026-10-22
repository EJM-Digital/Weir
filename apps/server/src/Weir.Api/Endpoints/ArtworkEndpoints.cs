using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Artwork;
using Weir.Infrastructure.Artwork;

namespace Weir.Api.Endpoints;

/// <summary>
/// <c>GET /api/v1/artwork/posters/{poster_id}</c>: a poster image Weir already holds. The browser never asks the metadata
/// service itself; the files list says where Weir serves each poster (<c>poster_url</c>).
/// </summary>
public static class ArtworkEndpoints
{
    public static IEndpointRouteBuilder MapArtworkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ArtworkEndpointHandlers>();
        endpoints.MapV1("GET", "/artwork/posters/{poster_id}", handlers.GetPosterAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="ArtworkEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ArtworkEndpointHandlers
{
    /// <summary>A poster never changes under its id, so a browser keeps it for 30 days.</summary>
    private const string PosterCacheControl = "private, max-age=2592000, immutable";

    private const string NoSuchPoster = "Weir has no poster with that id.";

    private readonly ArtworkLookupStore _lookups;
    private readonly ArtworkPosterFiles _posterFiles;

    public ArtworkEndpointHandlers(ArtworkLookupStore lookups, ArtworkPosterFiles posterFiles)
    {
        _lookups = lookups;
        _posterFiles = posterFiles;
    }

    public async Task<ApiResult> GetPosterAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var posterId = request.RouteValue("poster_id") ?? string.Empty;
        if (!ArtworkKeys.IsPosterId(posterId))
        {
            throw new ApiException(StatusCodes.Status404NotFound, NoSuchPoster);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        var poster = await _lookups.FindPosterAsync(uow, posterId).ConfigureAwait(false);
        var image = poster is null ? null : _posterFiles.Open(posterId);
        if (poster is null || image is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, NoSuchPoster);
        }

        return new CustomApiResult(async context =>
        {
            await using (image.ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = poster.ContentType;
                context.Response.ContentLength = image.Length;
                context.Response.Headers.CacheControl = PosterCacheControl;
                await image.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
        });
    }
}
