using System.Text.Json.Nodes;
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
    private readonly List<(string Request, string? Body)> _bodies = [];

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
                    var line = $"{request.Method} {new Uri(request.Url).AbsolutePath}";
                    sent._requests.Add(line);
                    sent._bodies.Add((line, request.PostData));
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

    /// <summary>The JSON each request of this kind ("METHOD /path") carried, in the order they were sent, without the token that proves the request came from the page.</summary>
    public async Task<IReadOnlyList<JsonObject>> BodiesAsync(string request)
    {
        await _page.EvaluateAsync("() => 0");
        lock (_requests)
        {
            return [.. _bodies
                .Where(sent => sent.Request == request && sent.Body is not null)
                .Select(sent =>
                {
                    var body = JsonNode.Parse(sent.Body!)!.AsObject();
                    body.Remove("csrf_token");
                    return body;
                })];
        }
    }
}
