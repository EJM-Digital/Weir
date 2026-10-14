using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Holds a report Weir sends Deluno to the one Deluno recorded Weir sending for the same kind of ending: the same members, in the same
/// order, each of the same JSON type, and the exact value of those that are words Deluno acts on (<c>status</c>, <c>failureClass</c>,
/// <c>disposition</c>, <c>sourceRemoved</c>, the processor's name) and of the ids and paths that name the hand-off and its output. The
/// sentences in <c>message</c> are Weir's own wording and are held to being text. A report that differs fails with the recording's file
/// and the JSON path of each member that does.
/// </summary>
internal static class EmittedReport
{
    /// <summary>Members whose value is part of the contract, not just its type.</summary>
    private static readonly HashSet<string> Exact =
    [
        "handoffId", "status", "processorName", "libraryId", "releaseName", "outputPath", "outputFiles", "disposition", "sourceRemoved", "failureClass",
    ];

    /// <summary>Fails with the recording's file and each difference when <paramref name="sent"/> is not the report <paramref name="exchange"/> records.</summary>
    public static void AssertMatches(Recording recording, int exchange, JsonObject sent)
    {
        var recorded = recording.Exchanges[exchange].Request;
        var differences = Differences((JsonObject)recorded.Body!, sent);
        if (differences.Count > 0)
        {
            throw new XunitException(
                $"Weir no longer reports as Deluno recorded.{Environment.NewLine}" +
                $"  recording: {recording.File}, exchange {exchange}: {recorded.Method} {recorded.Path}{Environment.NewLine}" +
                string.Join(Environment.NewLine, differences.Select(difference => "  " + difference)));
        }
    }

    public static List<string> Differences(JsonObject recorded, JsonObject sent)
    {
        var found = new List<string>();
        var recordedNames = recorded.Select(member => member.Key).ToList();
        var sentNames = sent.Select(member => member.Key).ToList();
        foreach (var name in recordedNames.Except(sentNames))
        {
            found.Add($"$.{name}: recorded as {Show(recorded[name])}, but Weir sent no such member");
        }

        foreach (var name in sentNames.Except(recordedNames))
        {
            found.Add($"$.{name}: Weir sent {Show(sent[name])}, which the recording does not have");
        }

        if (found.Count == 0 && !recordedNames.SequenceEqual(sentNames))
        {
            found.Add($"$: the recording has the members in the order [{string.Join(", ", recordedNames)}], Weir sent them in the order [{string.Join(", ", sentNames)}]");
        }

        foreach (var name in recordedNames.Intersect(sentNames))
        {
            Compare($"$.{name}", recorded[name], sent[name], Exact.Contains(name), found);
        }

        return found;
    }

    private static void Compare(string path, JsonNode? recorded, JsonNode? sent, bool exact, List<string> found)
    {
        if (Kind(recorded) != Kind(sent))
        {
            found.Add($"{path}: recorded {Show(recorded)} ({Kind(recorded)}), Weir sent {Show(sent)} ({Kind(sent)})");
            return;
        }

        if (recorded is JsonArray recordedItems && sent is JsonArray sentItems)
        {
            if (recordedItems.Count != sentItems.Count)
            {
                found.Add($"{path}: recorded {recordedItems.Count} item(s) {Show(recorded)}, Weir sent {sentItems.Count} {Show(sent)}");
                return;
            }

            for (var index = 0; index < recordedItems.Count; index++)
            {
                Compare($"{path}[{index}]", recordedItems[index], sentItems[index], exact, found);
            }

            return;
        }

        if (exact && !JsonNode.DeepEquals(recorded, sent))
        {
            found.Add($"{path}: recorded {Show(recorded)}, Weir sent {Show(sent)}");
        }
    }

    private static JsonValueKind Kind(JsonNode? node) => node?.GetValueKind() switch
    {
        null => JsonValueKind.Null,
        JsonValueKind.False => JsonValueKind.True,
        var kind => kind.Value,
    };

    private static string Show(JsonNode? node) => node?.ToJsonString() ?? "null";
}
