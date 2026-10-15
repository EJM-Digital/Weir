using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Tests.Shared;

namespace Weir.Api.Tests;

/// <summary>
/// <c>docs/coverage/route-access.json</c> says who may call each route, and the contract suite walks the running server against it. These
/// tests keep the file whole from the other side: the server's own endpoint table and the file list the same routes, so a route added
/// without a line in the file, or left in the file after it was removed, fails here instead of going unwalked.
/// </summary>
public sealed class RouteAccessTableTests
{
    [Fact]
    public async Task The_server_maps_exactly_the_routes_the_access_table_lists()
    {
        await using var server = await WeirTestServer.StartAsync();

        var mapped = MappedRoutes(server.Services);
        var listed = RouteAccessTable.Rules.Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);

        var unlisted = mapped.Except(listed).Order(StringComparer.Ordinal).ToList();
        var gone = listed.Except(mapped).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            unlisted.Count == 0,
            "The server maps routes docs/coverage/route-access.json does not list; add each with who may call it: " + string.Join(", ", unlisted));
        Assert.True(
            gone.Count == 0,
            "docs/coverage/route-access.json lists routes the server no longer maps; remove them: " + string.Join(", ", gone));
    }

    /// <summary>Every method and path the server maps, as <c>METHOD /path</c>. HEAD is left out: a HEAD request is the GET route's own answer.</summary>
    private static HashSet<string> MappedRoutes(IServiceProvider services) =>
        services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                    .Where(method => !HttpMethods.IsHead(method))
                    .Select(method => $"{method.ToUpperInvariant()} {endpoint.RoutePattern.RawText}"))
            .ToHashSet(StringComparer.Ordinal);
}
