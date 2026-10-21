using Weir.Api.OpenApi;

namespace Weir.Api.Tests;

/// <summary>The document a server serves lists only what the server answers: an operation it does not map is left out, and so is a path with none left.</summary>
public sealed class OpenApiPruningTests
{
    private const string Pause = "/api/v1/pause";

    [Fact]
    public void A_route_the_server_maps_with_fewer_methods_than_the_document_keeps_only_those_methods()
    {
        var document = WeirOpenApiDocument.Build([(Pause, ["get"])], "9.9.9");

        var operations = document["paths"]![Pause]!.AsObject().Select(entry => entry.Key).ToList();
        Assert.Contains("get", operations);
        Assert.DoesNotContain("put", operations);
    }

    [Fact]
    public void A_path_the_server_does_not_map_at_all_is_left_out_and_the_version_is_the_servers()
    {
        var document = WeirOpenApiDocument.Build([(Pause, ["GET", "PUT"])], "9.9.9");

        Assert.Equal([Pause], document["paths"]!.AsObject().Select(entry => entry.Key));
        Assert.Equal("9.9.9", document["info"]!["version"]!.GetValue<string>());
    }
}
