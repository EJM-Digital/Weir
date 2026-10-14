using System.Web;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>Makes a fake Deluno answer as Deluno's own recordings of its replies (<c>deluno-side/1.0.0-rc.30.32</c>) say it does.</summary>
internal static class RecordedDeluno
{
    /// <summary>
    /// Answers each request the recording holds with the reply Deluno recorded, placeholders filled in. A route with several recorded
    /// replies (the same path asked for every library, then for one) picks the one whose query matches the request; a request no
    /// recorded reply matches is answered 404 with a message naming it, so Weir asking for something Deluno was not recorded answering
    /// fails loudly. <paramref name="exchanges"/> limits the replay to these positions; <paramref name="anyQuery"/> answers a route with
    /// its recorded reply whatever query Weir sends (for a recording made without the query Weir adds). <paramref name="asRoute"/>
    /// serves the replies to a different call than the one they were recorded for: a refusal Deluno recorded for one route is the
    /// refusal its other routes make too, and this is how Weir is shown it where it would meet it. <paramref name="withoutBody"/> drops
    /// the reply's body, which is how an older Deluno answers a route it does not have.
    /// </summary>
    public static void Serve(
        this FakeManager deluno,
        Recording recording,
        DelunoPlaceholders placeholders,
        int[]? exchanges = null,
        bool anyQuery = false,
        (string Method, string Path)? asRoute = null,
        bool withoutBody = false)
    {
        var served = recording.Exchanges
            .Where((_, index) => exchanges is null || exchanges.Contains(index))
            .Select(exchange => asRoute is { } route
                ? (route.Method, Target: route.Path, exchange.Reply)
                : (exchange.Request.Method, Target: placeholders.Apply(exchange.Request.Path), exchange.Reply))
            .ToList();
        foreach (var route in served.GroupBy(exchange => (exchange.Method, Path: exchange.Target.Split('?')[0])))
        {
            var replies = route.ToList();
            deluno.Route(route.Key.Method, route.Key.Path, request =>
            {
                var match = replies.FirstOrDefault(reply => anyQuery || QueryOf(reply.Target) == Normalised(request));
                return match.Reply is null
                    ? new Reply(404, new System.Text.Json.Nodes.JsonObject { ["message"] = $"{recording.File} holds no reply to {request.Method} {request.Path}?{Normalised(request)}" })
                    : new Reply(
                        match.Reply.Status,
                        withoutBody ? null : placeholders.Apply(match.Reply.Body),
                        match.Reply.Headers
                            .Where(header => header.Key != "content-type")
                            .ToDictionary(header => header.Key, header => placeholders.Apply(header.Value)));
            });
        }
    }

    private static string QueryOf(string target) => target.Contains('?') ? Sorted(HttpUtility.ParseQueryString(target[(target.IndexOf('?') + 1)..])) : string.Empty;

    private static string Normalised(RecordedRequest request) =>
        string.Join('&', request.Query.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.Select(value => $"{pair.Key}={value}")));

    private static string Sorted(System.Collections.Specialized.NameValueCollection query) =>
        string.Join('&', query.AllKeys.OfType<string>().Order(StringComparer.Ordinal).SelectMany(key => query.GetValues(key)!.Select(value => $"{key}={value}")));
}
