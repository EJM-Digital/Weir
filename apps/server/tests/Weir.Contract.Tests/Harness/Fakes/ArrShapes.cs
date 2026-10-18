using System.Text.Json.Nodes;
using Weir.Tests.Shared;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>
/// What a Sonarr or Radarr says, taken from what a real one said (<c>fixtures/recorded/sonarr</c> and <c>radarr</c>), for the fake manager
/// to answer with: its status, its root folders, its queue page and rows, its command answer and its library rows. A test that needs a row
/// in a state the recording does not show (a download that has finished, a file at another path) names the members it changes; every
/// other member is the real app's.
/// </summary>
internal static class ArrShapes
{
    /// <summary>The app's answer to <c>GET /api/v3/system/status</c>.</summary>
    public static JsonNode Status(string kind) => Reply(kind, "connection-test");

    /// <summary>The app's root folders for these paths: the real root folder, once for each path with its own path and id.</summary>
    public static JsonArray RootFolders(string kind, IEnumerable<string> paths)
    {
        var template = Reply(kind, "describe").AsArray()[0]!.AsObject();
        return new JsonArray([.. paths.Select((path, index) =>
        {
            var folder = template.DeepClone().AsObject();
            folder["path"] = path;
            folder["id"] = index + 1;
            return (JsonNode)folder;
        })]);
    }

    /// <summary>The app's queue page holding these rows.</summary>
    public static JsonObject QueuePage(string kind, IReadOnlyList<JsonObject> records)
    {
        var page = Reply(kind, "queue-empty").AsObject();
        page["totalRecords"] = records.Count;
        page["records"] = new JsonArray([.. records.Select(record => (JsonNode)record.DeepClone())]);
        return page;
    }

    /// <summary>A queue row the app gave for a download in progress, with the named members changed.</summary>
    public static JsonObject QueueRow(string kind, params (string Name, JsonNode? Value)[] changes)
    {
        var row = ArrRecording.Load(kind, "queue-with-download").Only.Reply.Body!["records"]![0]!.DeepClone().AsObject();
        foreach (var (name, value) in changes)
        {
            row[name] = value;
        }

        return row;
    }

    /// <summary>The app's answer to a command it accepted, named <paramref name="name"/>.</summary>
    public static JsonObject CommandAccepted(string kind, string name)
    {
        var accepted = ArrRecording.Load(kind, "rescan").To("POST", "/api/v3/command").Reply.Body!.DeepClone().AsObject();
        accepted["name"] = name;
        accepted["body"]!["name"] = name;
        return accepted;
    }

    /// <summary>A movie (Radarr) or episode file (Sonarr) row as the library listing gives it, with the file at <paramref name="path"/>.</summary>
    public static JsonObject LibraryRow(string kind, string path)
    {
        var listing = ArrRecording.Load(kind, "library-listing");
        if (kind == "radarr")
        {
            var movie = listing.Exchanges[0].Reply.Body!.AsArray()[0]!.DeepClone().AsObject();
            movie["movieFile"]!["path"] = path;
            return movie;
        }

        var episode = listing.Exchanges.First(exchange => exchange.Request.Path == "/api/v3/episodefile" && exchange.Reply.Body is JsonArray { Count: > 0 })
            .Reply.Body!.AsArray()[0]!.DeepClone().AsObject();
        episode["path"] = path;
        return episode;
    }

    private static JsonNode Reply(string kind, string state) => ArrRecording.Load(kind, state).Exchanges[^1].Reply.Body!.DeepClone();
}
