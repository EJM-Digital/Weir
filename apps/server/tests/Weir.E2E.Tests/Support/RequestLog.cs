using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>One request the page sent to Weir's API: its verb, its path and query, and the body it carried.</summary>
public sealed record SentRequest(string Method, string PathAndQuery, string? Body)
{
    public override string ToString() => $"{Method} {PathAndQuery} {Body}".TrimEnd();
}

/// <summary>
/// Every request a page sends to Weir's API that can change something (anything but a read), as the browser sends it. A test
/// takes a <see cref="Mark"/> once a dialog is open and asks <see cref="Since"/> afterwards what the page sent since: for a
/// dialog that was cancelled the answer is nothing, and for one that was confirmed it is exactly the request the dialog is for.
/// </summary>
public sealed class RequestLog
{
    private static readonly string[] Reads = ["GET", "HEAD", "OPTIONS"];

    private readonly object _gate = new();
    private readonly List<SentRequest> _sent = [];

    private RequestLog()
    {
    }

    /// <summary>Starts recording what <paramref name="page"/> sends from now on.</summary>
    public static RequestLog Watch(IPage page)
    {
        var log = new RequestLog();
        page.Request += (_, request) => log.Note(request);
        return log;
    }

    /// <summary>A place in the log, for <see cref="Since"/>.</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _sent.Count;
        }
    }

    /// <summary>What the page sent after <paramref name="mark"/>, oldest first.</summary>
    public IReadOnlyList<SentRequest> Since(int mark)
    {
        lock (_gate)
        {
            return [.. _sent.Skip(mark)];
        }
    }

    private void Note(IRequest request)
    {
        var url = new Uri(request.Url);
        if (Reads.Contains(request.Method, StringComparer.OrdinalIgnoreCase) || !url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            _sent.Add(new SentRequest(request.Method, url.PathAndQuery, request.PostData));
        }
    }
}
