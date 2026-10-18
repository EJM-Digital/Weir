using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness.Fakes;

public sealed partial class FakeManager
{
    /// <summary>The queue records a Sonarr or Radarr fake answers with; the DELETE route removes from it.</summary>
    public SharedList<JsonObject> Queue { get; } = new();

    /// <summary>The movies (Radarr) or episode files (Sonarr) a fake answers its library route with.</summary>
    public SharedList<JsonObject> Library { get; } = new();

    /// <summary>The libraries a Deluno fake lists in its manifest.</summary>
    public SharedList<JsonObject> Libraries { get; } = new();

    /// <summary>The capabilities a Deluno fake lists in its manifest.</summary>
    public SharedList<string> Capabilities { get; } = new();

    /// <summary>The jobs a Deluno fake lists in its external queue.</summary>
    public SharedList<JsonObject> Jobs { get; } = new();

    /// <summary>
    /// A Sonarr or Radarr v3 fake: status, root folders, a queue a test fills in (<see cref="Queue"/>), command and manual import,
    /// each answered in the shape a real Sonarr or Radarr gave (<see cref="ArrShapes"/>). It is already running.
    /// </summary>
    public static FakeManager StartArr(string kind, IEnumerable<string>? rootFolders = null)
    {
        if (kind is not ("sonarr" or "radarr"))
        {
            throw new ArgumentException("kind must be sonarr or radarr", nameof(kind));
        }

        var fake = new FakeManager(kind, DefaultApiKey);
        var folders = rootFolders?.ToList() ?? [];
        fake.Route("GET", "/api/v3/system/status", _ => Reply.Ok(ArrShapes.Status(kind)));
        fake.Route("GET", "/api/v3/rootfolder", _ => Reply.Ok(ArrShapes.RootFolders(kind, folders)));
        fake.Route("GET", "/api/v3/queue", _ => Reply.Ok(ArrShapes.QueuePage(kind, fake.Queue.Snapshot())));
        fake.Route("DELETE", "/api/v3/queue/{id}", request => fake.RemoveFromQueue(request.Path[(request.Path.LastIndexOf('/') + 1)..]));
        fake.Route("POST", "/api/v3/command", request => new Reply(201, ArrShapes.CommandAccepted(kind, (string?)(request.Json as JsonObject)?["name"] ?? "unknown")));
        fake.Route("GET", "/api/v3/manualimport", new JsonArray());
        fake.Route("GET", kind == "radarr" ? "/api/v3/movie" : "/api/v3/episodefile", _ => Reply.Ok(Copy(fake.Library.Snapshot())));
        return fake;
    }

    /// <summary>Deluno's external-integration surface and its processor events endpoint. It is already running.</summary>
    public static FakeManager StartDeluno(IEnumerable<JsonObject>? libraries = null, IEnumerable<string>? capabilities = null)
    {
        var fake = new FakeManager("deluno", DefaultApiKey);
        fake.Libraries.Replace(libraries ?? []);
        fake.Capabilities.Replace(capabilities ?? []);
        fake.Route("GET", "/api/integrations/external/health", new JsonObject { ["status"] = "ok" });
        fake.Route("GET", "/api/integrations/external/manifest", _ => Reply.Ok(new JsonObject
        {
            ["name"] = "Deluno",
            ["libraries"] = Copy(fake.Libraries.Snapshot()),
            ["capabilities"] = new JsonArray(fake.Capabilities.Snapshot().Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()),
        }));
        fake.Route("GET", "/api/integrations/external/queue", _ => Reply.Ok(new JsonObject { ["jobs"] = Copy(fake.Jobs.Snapshot()), ["dispatches"] = new JsonArray() }));
        fake.Route("POST", "/api/integrations/processors/events", new JsonObject { ["accepted"] = true }, status: 202);
        return fake;
    }

    private Reply RemoveFromQueue(string id) =>
        new(Queue.RemoveAll(row => row["id"]?.ToString() == id) > 0 ? 200 : 404);

    // A node belongs to one parent, so every answer is built from copies of what the test put in the lists.
    private static JsonArray Copy(IEnumerable<JsonObject> rows) => new(rows.Select(row => row.DeepClone()).ToArray());
}
