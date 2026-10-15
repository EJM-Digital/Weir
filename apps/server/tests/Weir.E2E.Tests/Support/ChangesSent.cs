using Microsoft.Playwright;

namespace Weir.E2E.Tests.Support;

/// <summary>
/// The requests a page sends that could change something on the server (anything but a read), as the browser makes them. A test that
/// cancels a dialog reads it to say nothing was sent, whatever the screen then shows.
/// </summary>
public sealed class ChangesSent
{
    private static readonly string[] Reads = ["GET", "HEAD", "OPTIONS"];

    private readonly IPage _page;
    private readonly List<string> _requests = [];

    private ChangesSent(IPage page) => _page = page;

    /// <summary>Starts recording what <paramref name="page"/> sends from now on.</summary>
    public static ChangesSent Watch(IPage page)
    {
        var sent = new ChangesSent(page);
        page.Request += (_, request) =>
        {
            if (!Reads.Contains(request.Method, StringComparer.OrdinalIgnoreCase))
            {
                lock (sent._requests)
                {
                    sent._requests.Add($"{request.Method} {new Uri(request.Url).AbsolutePath}");
                }
            }
        };
        return sent;
    }

    /// <summary>
    /// What was sent so far, as "METHOD /path". The browser reports its requests in order, so a round trip to the page first means
    /// everything it sent before this call is in the list.
    /// </summary>
    public async Task<IReadOnlyList<string>> SoFarAsync()
    {
        await _page.EvaluateAsync("() => 0");
        lock (_requests)
        {
            return [.. _requests];
        }
    }
}
