using System.Text.Json.Nodes;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// Deluno's own answer to a report Weir sent it, as Deluno recorded it (<c>fixtures/recorded/deluno-side/</c>), for the fake Deluno to
/// answer with: the recorded status, content type and body, and nothing written by hand.
/// </summary>
internal static class RecordedDelunoReply
{
    /// <summary>The answer in the first exchange of the recording <paramref name="state"/> in the <paramref name="version"/> folder.</summary>
    public static DelunoReply Of(string version, string state)
    {
        var path = Path.Combine(FixturesFolder(), version, state + ".json");
        var reply = JsonNode.Parse(File.ReadAllText(path))!["exchanges"]![0]!["reply"]!;
        var contentType = ((string?)reply["headers"]?["content-type"])?.Split(';')[0] ?? "application/json";
        var body = reply["body"]?.ToJsonString() ?? (string?)reply["bodyText"] ?? string.Empty;
        return new DelunoReply((int)reply["status"]!, body, contentType);
    }

    private static string FixturesFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "apps", "server", "tests", "fixtures", "recorded", "deluno-side");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The recordings of Deluno's side are not above the test run.");
    }
}
