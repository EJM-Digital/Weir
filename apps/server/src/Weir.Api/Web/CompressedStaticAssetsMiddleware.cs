using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Weir.Api.Http;
using Weir.Core.Configuration;

namespace Weir.Api.Web;

/// <summary>
/// Serves hashed <c>/assets/*</c> files with pre-compressed variants and immutable caching.
/// It sits outside the other middleware, so it applies the baseline security headers itself
/// rather than reaching <see cref="Weir.Api.Http.SecurityHeadersMiddleware"/>; it still carries
/// no <c>X-Request-ID</c>.
/// </summary>
/// <remarks>
/// The ETag is derived from the served file's modification time and size, so it changes whenever the file does.
/// </remarks>
public sealed class CompressedStaticAssetsMiddleware
{
    public const string CacheControl = "public, max-age=31536000, immutable";

    private static readonly HashSet<string> CompressibleSuffixes = new(StringComparer.Ordinal)
    {
        ".css", ".html", ".js", ".json", ".map", ".svg", ".txt", ".wasm", ".xml",
    };

    private readonly RequestDelegate _next;
    private readonly WebDist _webDist;
    private readonly WeirOptions _options;
    private readonly FileExtensionContentTypeProvider _contentTypes = WebContentTypes.CreateProvider();

    public CompressedStaticAssetsMiddleware(RequestDelegate next, WebDist webDist, WeirOptions options)
    {
        _next = next;
        _webDist = webDist;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        // Range requests keep the static file server's range semantics: a compressed
        // representation cannot answer a byte range of the source file.
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) ||
            request.Headers.ContainsKey(HeaderNames.Range) ||
            _webDist.Root is not { } root ||
            AssetPath(root, request.Path.ToString()) is not { } path)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? encoding = null;
        var served = path;
        if (CompressibleSuffixes.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            var accept = request.Headers.AcceptEncoding.ToString();
            foreach (var candidate in new[] { "br", "gzip" })
            {
                var compressed = $"{path}.{(candidate == "gzip" ? "gz" : candidate)}";
                if (AcceptsEncoding(accept, candidate) && File.Exists(compressed))
                {
                    served = compressed;
                    encoding = candidate;
                    break;
                }
            }
        }

        var file = new FileInfo(served);
        var etag = ETag(file, encoding);
        var response = context.Response;
        response.Headers.CacheControl = CacheControl;
        response.Headers.ETag = etag;
        response.Headers.Vary = "Accept-Encoding";
        response.ContentType = WebContentTypes.For(_contentTypes, Path.GetFileName(path), "application/octet-stream");
        if (encoding is not null)
        {
            response.Headers.ContentEncoding = encoding;
        }

        // This middleware answers before UseRouting, so SecurityHeadersMiddleware never runs for it.
        SecurityHeadersMiddleware.Apply(context, _options.SecurityEnableHsts);

        if (request.Headers.IfNoneMatch.ToString().Trim() == etag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentLength = file.Length;
        response.Headers.AcceptRanges = "bytes";
        response.Headers.LastModified = file.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture);
        if (HttpMethods.IsHead(request.Method))
        {
            return;
        }

        await response.SendFileAsync(served, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The file under <c>{root}/assets</c> a request path names, or <see langword="null"/>.</summary>
    internal static string? AssetPath(string root, string urlPath)
    {
        if (!urlPath.StartsWith("/assets/", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = urlPath[1..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".." || part.Contains('\\', StringComparison.Ordinal)))
        {
            return null;
        }

        var assetsRoot = Path.GetFullPath(Path.Join(root, "assets"));
        var candidate = Path.GetFullPath(Path.Join([root, .. parts]));
        // Both paths are built from the same root and the request path starts with "assets" exactly, so a match is an exact one on any system.
        if (!candidate.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Whether an <c>Accept-Encoding</c> value allows <paramref name="encoding"/> (the first matching entry decides).</summary>
    internal static bool AcceptsEncoding(string raw, string encoding)
    {
        var wildcard = false;
        foreach (var item in raw.ToLowerInvariant().Split(','))
        {
            var separator = item.IndexOf(';', StringComparison.Ordinal);
            var name = (separator < 0 ? item : item[..separator]).Trim();
            var options = separator < 0 ? string.Empty : item[(separator + 1)..];
            var q = 1.0;
            foreach (var option in options.Split(';'))
            {
                var equals = option.IndexOf('=', StringComparison.Ordinal);
                var key = (equals < 0 ? option : option[..equals]).Trim();
                if (key != "q")
                {
                    continue;
                }

                var value = equals < 0 ? string.Empty : option[(equals + 1)..].Trim();
                q = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.0;
            }

            if (name == encoding)
            {
                return q > 0;
            }

            if (name == "*")
            {
                wildcard = q > 0;
            }
        }

        return wildcard;
    }

    private static string ETag(FileInfo file, string? encoding)
    {
        var material = Encoding.ASCII.GetBytes(
            $"{file.LastWriteTimeUtc.Ticks}:{file.Length}{(encoding is null ? string.Empty : "-" + encoding)}");
        return $"\"{Convert.ToHexStringLower(SHA256.HashData(material))[..24]}\"";
    }
}
