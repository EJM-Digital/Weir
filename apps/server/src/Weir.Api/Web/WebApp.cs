using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Weir.Api.Http;

namespace Weir.Api.Web;

/// <summary>The built web app directory (<c>WEIR_WEB_DIST</c>).</summary>
/// <param name="Root">Absolute path, or <see langword="null"/> when <c>WEIR_WEB_DIST</c> is unset.</param>
public sealed record WebDist(string? Root)
{
    /// <summary>Checked per request: the directory can appear or disappear while running.</summary>
    public string? IndexFile =>
        Root is not null && Directory.Exists(Root) && File.Exists(Path.Join(Root, "index.html"))
            ? Path.Join(Root, "index.html")
            : null;

    /// <summary>Whether the app was servable at startup; static files and asset middleware are wired only then.</summary>
    public bool MountedAtStartup { get; init; }
}

/// <summary>
/// Serves the React app: <c>/</c> and <c>/index.html</c> with no-cache headers, other files from the dist
/// directory, the app shell for browser refreshes on client-side routes, and a JSON
/// <c>{"detail": "Not Found"}</c> 404 for everything else.
/// </summary>
public static class WebApp
{
    private static readonly string[] GetAndHead = [HttpMethods.Get, HttpMethods.Head];
    private static readonly string[] NonDocumentPrefixes = ["/api", "/health", "/ready", "/metrics"];
    private static readonly string[] UpgradeTokens = ["update-now", "upgrade-now", "upgrade"];

    public static WebDist Resolve(string? configuredRoot)
    {
        var dist = new WebDist(configuredRoot);
        return dist with { MountedAtStartup = dist.IndexFile is not null };
    }

    /// <summary>Static files for the mounted dist directory. Runs after routing, so API routes win.</summary>
    public static IApplicationBuilder UseWebAppStaticFiles(this IApplicationBuilder app, WebDist webDist)
    {
        ArgumentNullException.ThrowIfNull(webDist);
        if (!webDist.MountedAtStartup || webDist.Root is null)
        {
            return app;
        }

        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(webDist.Root),
            ContentTypeProvider = WebContentTypes.CreateProvider(),
            ServeUnknownFileTypes = true,
            DefaultContentType = "text/plain",
            OnPrepareResponse = prepared =>
            {
                var response = prepared.Context.Response;
                if (response.ContentType is { } contentType)
                {
                    response.ContentType = WebContentTypes.WithCharset(contentType);
                }
            },
        });
    }

    public static IEndpointRouteBuilder MapWebAppIndex(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/", GetAndHead, ServeIndexOrNotFoundAsync);
        endpoints.MapMethods("/index.html", GetAndHead, ServeIndexOrNotFoundAsync);
        return endpoints;
    }

    /// <summary>
    /// The end of the pipeline: nothing else answered. Answers the JSON 404, or a 405 for a non-GET/HEAD
    /// request outside <c>/api</c> while the web app is served, as existing clients expect.
    /// </summary>
    public static async Task HandleUnmatchedAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var webDist = context.RequestServices.GetRequiredService<WebDist>();
        var request = context.Request;
        var isGet = HttpMethods.IsGet(request.Method);
        var isHead = HttpMethods.IsHead(request.Method);

        // An /api path never falls through to the web app (#535). Known API paths with the
        // wrong method are answered 405 by routing; anything else under /api is a JSON 404.
        var isApi = request.Path.StartsWithSegments("/api", StringComparison.Ordinal);
        if (webDist.MountedAtStartup && !isGet && !isHead && !isApi)
        {
            await ApiJson.WriteDetailAsync(context, StatusCodes.Status405MethodNotAllowed, "Method Not Allowed").ConfigureAwait(false);
            return;
        }

        if (IsUpgradeBrowserLanding(request))
        {
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = "/system?tab=about";
            return;
        }

        if ((isGet || isHead) && IsSpaHistoryRequest(request) && webDist.IndexFile is { } index)
        {
            await WriteIndexAsync(context, index).ConfigureAwait(false);
            return;
        }

        await ApiJson.WriteDetailAsync(context, StatusCodes.Status404NotFound, "Not Found").ConfigureAwait(false);
    }

    /// <summary>
    /// Stale in-app-upgrade browser landings (<c>/api/…upgrade…</c>) go back to System › About instead of a
    /// useless JSON 404 while the app restarts.
    /// </summary>
    internal static bool IsUpgradeBrowserLanding(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.ToString().ToLowerInvariant();
        return path.StartsWith("/api", StringComparison.Ordinal) &&
            UpgradeTokens.Any(token => path.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>A browser document request for a client-side route: no file suffix, not an API or probe path.</summary>
    internal static bool IsSpaHistoryRequest(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString().ToLowerInvariant();
        var secFetchDest = request.Headers["Sec-Fetch-Dest"].ToString().ToLowerInvariant();
        if (!accept.Contains("text/html", StringComparison.Ordinal) && secFetchDest != "document")
        {
            return false;
        }

        var path = request.Path.ToString();
        return !NonDocumentPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)) &&
            PathSuffix(path).Length == 0;
    }

    /// <summary>The last dot-suffix of the final path component; none for a dot file such as <c>.env</c>.</summary>
    internal static string PathSuffix(string path)
    {
        var name = path.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..] : string.Empty;
    }

    private static async Task ServeIndexOrNotFoundAsync(HttpContext context)
    {
        var index = context.RequestServices.GetRequiredService<WebDist>().IndexFile;
        if (index is null)
        {
            await HandleUnmatchedAsync(context).ConfigureAwait(false);
            return;
        }

        await WriteIndexAsync(context, index).ConfigureAwait(false);
    }

    private static async Task WriteIndexAsync(HttpContext context, string index)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
        var file = new FileInfo(index);
        var result = TypedResults.PhysicalFile(
            index,
            "text/html; charset=utf-8",
            lastModified: file.LastWriteTimeUtc,
            enableRangeProcessing: true);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }
}
