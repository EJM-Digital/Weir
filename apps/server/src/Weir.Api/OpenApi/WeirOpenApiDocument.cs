using System.Text.Json.Nodes;

namespace Weir.Api.OpenApi;

/// <summary>
/// Builds the served <c>/openapi.json</c>: the committed <c>apps/web/openapi/weir-openapi.json</c> (embedded at
/// build time, see the <c>EmbeddedResource</c> in Weir.Api.csproj), pruned to the operations
/// <see cref="Http.RouteTable"/> says the server maps.
/// </summary>
/// <remarks>
/// Pruning rather than generating keeps every schema identical to the document existing clients were generated
/// from (apps/server/README.md, "API contract"; ADR-0017). <c>OpenApiDocumentParityTests</c> checks the gap between the two
/// against an allowlist, so a mapped route dropping out or an unlisted gap appearing fails the build.
/// </remarks>
public static class WeirOpenApiDocument
{
    private const string ResourceName = "Weir.Api.OpenApi.committed-openapi.json";

    /// <summary>The lowercase JSON keys OpenAPI uses for HTTP operations under a path item.</summary>
    private static readonly HashSet<string> OperationKeys = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace",
    };

    private static readonly Lazy<JsonObject> CommittedDocument = new(LoadEmbeddedDocument);

    /// <summary>
    /// The committed document, pruned to the <paramref name="implementedRoutes"/> (a <see cref="Http.RouteTable"/>
    /// snapshot: each route's mounted path, which matches the document's path exactly, and the HTTP methods mapped
    /// to it), with <c>info.version</c> set to the running server's own version.
    /// </summary>
    public static JsonObject Build(IReadOnlyList<(string Path, IReadOnlyList<string> Methods)> implementedRoutes, string version)
    {
        ArgumentNullException.ThrowIfNull(implementedRoutes);
        ArgumentNullException.ThrowIfNull(version);
        var implemented = new HashSet<(string Method, string Path)>();
        foreach (var (path, methods) in implementedRoutes)
        {
            foreach (var method in methods)
            {
                implemented.Add((method.ToUpperInvariant(), path));
            }
        }

        var document = Clone(CommittedDocument.Value);
        var paths = document["paths"]!.AsObject();
        foreach (var path in paths.Select(entry => entry.Key).ToList())
        {
            var operations = paths[path]!.AsObject();
            foreach (var method in operations.Select(entry => entry.Key).Where(OperationKeys.Contains).ToList())
            {
                if (!implemented.Contains((method.ToUpperInvariant(), path)))
                {
                    operations.Remove(method);
                }
            }

            if (!operations.Select(entry => entry.Key).Any(OperationKeys.Contains))
            {
                paths.Remove(path);
            }
        }

        document["info"]!["version"] = version;
        return document;
    }

    private static JsonObject Clone(JsonObject document) => document.DeepClone().AsObject();

    private static JsonObject LoadEmbeddedDocument()
    {
        // The document is embedded by the build, and the parity tests fail the build of a release that lacks it or whose paths are not objects.
        using var stream = typeof(WeirOpenApiDocument).Assembly.GetManifestResourceStream(ResourceName)!;
        return JsonNode.Parse(stream)!.AsObject();
    }
}
