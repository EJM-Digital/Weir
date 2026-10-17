using Microsoft.Playwright;
using Weir.Contract.Tests.Harness;

namespace Weir.E2E.Tests.Support;

/// <summary>One request the page sent to Weir's API: its verb, its path and query, the body it carried, and the CSRF token it carried in a header, if any.</summary>
public sealed record SentRequest(string Method, string PathAndQuery, string? Body, string? CsrfHeader = null)
{
    public override string ToString() => $"{Method} {PathAndQuery} {Body}".TrimEnd();
}

/// <summary>
/// Every request a page sends to Weir's API that can change something (anything but a read), as the browser sends it. A test
/// takes a <see cref="Mark"/> once a dialog is open and asks <see cref="Since"/> afterwards what the page sent since: for a
/// dialog that was cancelled the answer is nothing, and for one that was confirmed it is exactly the request the dialog is for.
/// <para>Every change the page makes starts by fetching a fresh CSRF token, in the step of the click that asks for the change, and
/// the change itself follows a round trip later. So that a test that looks the moment a dialog has gone cannot miss a change that is
/// still on its way, the token fetches are logged too: <see cref="AssertNothingSentSince"/> fails on either, while
/// <see cref="Since"/> lists only the changes.</para>
/// </summary>
public sealed class RequestLog
{
    private const string TokenPath = "/api/v1/auth/csrf";
    private static readonly string[] Reads = ["GET", "HEAD", "OPTIONS"];

    private readonly object _gate = new();
    private readonly List<SentRequest> _logged = [];

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

    /// <summary>A place in the log, for <see cref="Since"/> and <see cref="AssertNothingSentSince"/>.</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _logged.Count;
        }
    }

    /// <summary>The changes the page sent after <paramref name="mark"/>, oldest first.</summary>
    public IReadOnlyList<SentRequest> Since(int mark)
    {
        lock (_gate)
        {
            return [.. _logged.Skip(mark).Where(request => request.PathAndQuery != TokenPath)];
        }
    }

    /// <summary>
    /// The one change the page sends after <paramref name="mark"/>, waited for: a control that answers at once (a switch that shows its new state) sends its change a round
    /// trip later, after a token. Fails when it has not come, or when it came more than once.
    /// </summary>
    public async Task<SentRequest> OnlyChangeSinceAsync(int mark, string what)
    {
        await Poll.UntilAsync(() => Task.FromResult(Since(mark).Count > 0), $"the page to send {what}");
        return Assert.Single(Since(mark));
    }

    /// <summary>
    /// Fails when the page sent a change after <paramref name="mark"/>, or began one by fetching a token to send it with.
    /// <paramref name="harmlessPathEnding"/> names a path that is a question and changes nothing, such as a preview.
    /// </summary>
    public void AssertNothingSentSince(int mark, string? harmlessPathEnding = null)
    {
        List<SentRequest> sent;
        lock (_gate)
        {
            sent = [.. _logged.Skip(mark).Where(request => harmlessPathEnding is null || !request.PathAndQuery.EndsWith(harmlessPathEnding, StringComparison.Ordinal))];
        }

        Assert.Empty(sent);
    }

    private void Note(IRequest request)
    {
        var url = new Uri(request.Url);
        var isToken = url.AbsolutePath == TokenPath;
        if (!url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) || (Reads.Contains(request.Method, StringComparer.OrdinalIgnoreCase) && !isToken))
        {
            return;
        }

        lock (_gate)
        {
            _logged.Add(new SentRequest(request.Method, url.PathAndQuery, request.PostData, request.Headers.GetValueOrDefault("x-csrf-token")));
        }
    }
}
