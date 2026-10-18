using System.Collections.Specialized;
using System.Web;
using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>One read the page asked Weir's API for: the path, the query parameters it carried and the header that says what it will take back.</summary>
public sealed record ReadSent(string Path, NameValueCollection Query, string? Accept)
{
    /// <summary>The query as "name=value" pairs by name, for a test that compares the whole of it.</summary>
    public IReadOnlyDictionary<string, string> Parameters =>
        Query.AllKeys.Where(name => name is not null).ToDictionary(name => name!, name => Query[name]!, StringComparer.Ordinal);

    public override string ToString() => $"GET {Path}?{Query}";
}

/// <summary>Every read a page makes of Weir's API, as the browser makes it, so a test can say exactly what was asked for.</summary>
public sealed class ReadsSent
{
    private readonly object _gate = new();
    private readonly List<ReadSent> _logged = [];

    private ReadsSent()
    {
    }

    /// <summary>Starts recording what <paramref name="page"/> asks for from now on.</summary>
    public static ReadsSent Watch(IPage page)
    {
        var reads = new ReadsSent();
        page.Request += (_, request) => reads.Note(request);
        return reads;
    }

    /// <summary>A place in the record, for <see cref="Since"/>.</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _logged.Count;
        }
    }

    /// <summary>The reads of <paramref name="path"/> after <paramref name="mark"/>, oldest first.</summary>
    public IReadOnlyList<ReadSent> Since(int mark, string path)
    {
        lock (_gate)
        {
            return [.. _logged.Skip(mark).Where(read => read.Path == path)];
        }
    }

    private void Note(IRequest request)
    {
        var url = new Uri(request.Url);
        if (request.Method != "GET" || !url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            _logged.Add(new ReadSent(url.AbsolutePath, HttpUtility.ParseQueryString(url.Query), request.Headers.GetValueOrDefault("accept")));
        }
    }
}
