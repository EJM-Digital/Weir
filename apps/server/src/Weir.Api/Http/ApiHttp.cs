using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Weir.Core.Json;
using Weir.Core.Validation;

namespace Weir.Api.Http;

/// <summary>An error answered with <c>{"detail": …}</c>, this status and these headers: the body shape existing clients read.</summary>
public sealed class ApiException : Exception
{
    public ApiException()
    {
        Detail = string.Empty;
    }

    public ApiException(string message)
        : base(message)
    {
        Detail = message;
    }

    public ApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Detail = message;
    }

    public ApiException(int statusCode, string detail, IReadOnlyDictionary<string, string>? headers = null)
        : base(detail)
    {
        StatusCode = statusCode;
        Detail = detail;
        Headers = headers;
    }

    public int StatusCode { get; } = StatusCodes.Status500InternalServerError;

    public string Detail { get; }

    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>
    /// A stable, machine-readable name for this error, sent as <c>code</c> beside <c>detail</c>. Set only where a client
    /// needs to act on which error it got; <c>detail</c> is wording for people and may change.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Members sent beside <c>detail</c> and <c>code</c>, for an error whose answer tells the client what stood in the way
    /// (the workflows that keep a connection from being removed, say). Never set a member named <c>detail</c> or <c>code</c>.
    /// </summary>
    public WireObject? Details { get; init; }
}

/// <summary>Writes JSON and plain-text responses with the exact bytes, content type and length existing clients expect.</summary>
public static class ApiResponses
{
    public static async Task WriteJsonAsync(HttpContext context, int statusCode, WireValue body)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bytes = WireJsonWriter.DumpsUtf8(body, WireJsonFormat.Response);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    public static async Task WritePlainTextAsync(HttpContext context, int statusCode, string text, string contentType = "text/plain; charset=utf-8")
    {
        ArgumentNullException.ThrowIfNull(context);
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    public static Task WriteDetailAsync(HttpContext context, ApiException exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);
        foreach (var (name, value) in exception.Headers ?? new Dictionary<string, string>())
        {
            context.Response.Headers[name] = value;
        }

        var body = new WireObject().Set("detail", exception.Detail);
        if (exception.Code is { } code)
        {
            body.Set("code", code);
        }

        foreach (var (name, value) in exception.Details?.Items ?? [])
        {
            body.Set(name, value);
        }

        return WriteJsonAsync(context, exception.StatusCode, body);
    }

    public static Task WriteValidationErrorAsync(HttpContext context, RequestValidationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return WriteJsonAsync(context, StatusCodes.Status422UnprocessableEntity, exception.ToBody());
    }

    /// <summary>A 204 with the JSON media type and no body, for a route that returns nothing (existing clients see this exact response).</summary>
    public static void NoContentJson(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.ContentType = "application/json";
    }
}

/// <summary>Reads a request body before validating it, with the error statuses and bodies existing clients expect.</summary>
public static class ApiRequestBody
{
    /// <summary>
    /// The largest body Weir reads. The biggest legitimate body is a configuration backup being restored, which is
    /// well under this; anything larger is refused before it is buffered, so a request cannot fill memory.
    /// </summary>
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    private const string TooLargeDetail = "The request body is larger than Weir accepts.";

    /// <summary>
    /// Every Weir route reads its body as JSON, so a non-empty body with no <c>Content-Type</c> or one that
    /// isn't JSON is refused rather than guessed at. This also forces a real CORS preflight for a
    /// cross-origin browser request, since a plain-text body needs none.
    /// </summary>
    public const string UnsupportedContentTypeDetail = "Send the body as application/json.";

    /// <summary>
    /// The JSON body, <see langword="null"/> when the request has none. A JSON syntax error is a 422
    /// <c>json_invalid</c>; undecodable bytes are a 400; a non-empty body whose <c>Content-Type</c> is
    /// missing or not JSON is a 415.
    /// </summary>
    public static async Task<WireValue?> ReadAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, TooLargeDetail);
        }

        var bytes = await ReadCappedAsync(context.Request.Body, context.RequestAborted).ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            return null;
        }

        var contentType = context.Request.Headers.ContentType.ToString();
        if (!IsJsonContentType(contentType))
        {
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, UnsupportedContentTypeDetail);
        }

        string text;
        try
        {
            text = WireJsonParser.DecodeBytes(bytes);
        }
        catch (WireJsonEncodingException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "There was an error parsing the body");
        }

        try
        {
            return WireJsonParser.Parse(text);
        }
        catch (WireJsonDecodeException exception)
        {
            throw new RequestValidationException(
            [
                new ValidationIssue(
                    "json_invalid", ["body", exception.Position], "JSON decode error", new WireObject(),
                    new WireObject().Set("error", exception.Detail)),
            ]);
        }
    }

    /// <summary>Reads the body, stopping with a 413 as soon as it passes <see cref="MaxBodyBytes"/>, whatever Content-Length said.</summary>
    private static async Task<byte[]> ReadCappedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, TooLargeDetail);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Whether the media type, parameters ignored, is <c>application/json</c> or <c>application/*+json</c>.</summary>
    internal static bool IsJsonContentType(string contentType)
    {
        var main = contentType.Split(';')[0].Trim().ToLowerInvariant();
        var slash = main.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || main.Count(c => c == '/') != 1)
        {
            return false;
        }

        var type = main[..slash];
        var subtype = main[(slash + 1)..];
        return type == "application" && (subtype == "json" || subtype.EndsWith("+json", StringComparison.Ordinal));
    }
}

/// <summary>Cookie parsing and <c>Set-Cookie</c> headers, byte-identical to what existing clients and the contract suite expect.</summary>
public static class CookieHeaders
{
    /// <summary>Splits on <c>;</c>; the last value for a name wins and quoted values are unquoted.</summary>
    public static Dictionary<string, string> Parse(StringValues cookieHeaders)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in cookieHeaders)
        {
            foreach (var chunk in (header ?? string.Empty).Split(';'))
            {
                string key;
                string value;
                var eq = chunk.IndexOf('=', StringComparison.Ordinal);
                if (eq >= 0)
                {
                    key = chunk[..eq];
                    value = chunk[(eq + 1)..];
                }
                else
                {
                    key = string.Empty;
                    value = chunk;
                }

                key = key.Trim();
                value = value.Trim();
                if (key.Length > 0 || value.Length > 0)
                {
                    cookies[key] = Unquote(value);
                }
            }
        }

        return cookies;
    }

    public static string? Get(HttpContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Parse(context.Request.Headers.Cookie).TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Strips the surrounding quotes and decodes backslash and three-digit octal escapes.</summary>
    internal static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var inner = value[1..^1];
        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                builder.Append(inner[i]);
                continue;
            }

            if (i + 3 < inner.Length && IsOctal(inner[i + 1]) && IsOctal(inner[i + 2]) && IsOctal(inner[i + 3]))
            {
                builder.Append((char)Convert.ToInt32(inner.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(inner[i + 1]);
                i++;
            }
        }

        return builder.ToString();
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';

    /// <summary>
    /// The <c>Set-Cookie</c> value with attributes in a fixed sorted order
    /// (<c>expires, HttpOnly, Max-Age, Path, SameSite, Secure</c>) so the header stays byte-identical for existing clients.
    /// </summary>
    public static string SetCookieHeader(string name, string value, long? maxAge, DateTimeOffset? expires, bool httpOnly, bool secure, string sameSite, string path = "/")
    {
        var builder = new StringBuilder();
        builder.Append(name).Append('=').Append(QuoteIfNeeded(value));
        if (expires is { } at)
        {
            builder.Append("; expires=").Append(at.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture));
        }

        if (httpOnly)
        {
            builder.Append("; HttpOnly");
        }

        if (maxAge is { } age)
        {
            builder.Append("; Max-Age=").Append(age.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append("; Path=").Append(path);
        builder.Append("; SameSite=").Append(sameSite);
        if (secure)
        {
            builder.Append("; Secure");
        }

        return builder.ToString();
    }

    /// <summary>Values outside the legal cookie character set are quoted, with octal escapes for the rest.</summary>
    private static string QuoteIfNeeded(string value)
    {
        const string legal = "!#$%&'*+-.^_`|~:";
        if (value.All(c => char.IsAsciiLetterOrDigit(c) || legal.Contains(c, StringComparison.Ordinal)))
        {
            return value.Length == 0 ? "\"\"" : value;
        }

        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c == '"' || c == '\\')
            {
                builder.Append('\\').Append(c);
            }
            else if (c is < ' ' or > '~' || c is ',' or ';')
            {
                builder.Append('\\').Append(Convert.ToString(c & 0xFF, 8).PadLeft(3, '0'));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.Append('"').ToString();
    }
}
