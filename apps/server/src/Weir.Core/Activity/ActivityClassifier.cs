using Weir.Core.Json;

namespace Weir.Core.Activity;

/// <summary>
/// The queryable facts about an activity event, lifted into columns when it is written (#469).
/// </summary>
public sealed record ActivityFacts(string? Trigger, string? Result, long? LibraryId, string? RelativePath, string? RunKey);

/// <summary>Derives an activity event's trigger, result, library, path and run key from its type and detail.</summary>
/// <remarks>
/// The detail is read with <see cref="WireJsonParser"/>, so details written by earlier releases parse the same
/// (<c>NaN</c>, integers of any size, the last duplicate key winning), and text is trimmed with
/// <see cref="WireStrings.Strip"/>.
/// </remarks>
public static class ActivityClassifier
{
    /// <summary>The longest path the <c>relative_path</c> column holds.</summary>
    public const int RelativePathLimit = 2000;

    /// <summary>Allowed triggers: <c>docs/operator-messaging-standard.md</c>, plus <c>webhook</c> and <c>folder_change</c>.</summary>
    public static readonly IReadOnlySet<string> Triggers = new HashSet<string>(StringComparer.Ordinal)
    {
        "manual", "scheduled", "startup", "worker", "retry", "system", "webhook", "folder_change",
    };

    /// <summary>Allowed results.</summary>
    public static readonly IReadOnlySet<string> Results = new HashSet<string>(StringComparer.Ordinal)
    {
        "success", "skipped", "warning", "retrying", "running", "failed",
    };

    private static readonly string[] PersonStartedPrefixes = ["auth.", "system.reconciliation.", "system.network_access.", "system.home_network_sign_in."];

    // Read from the event type only when the producer did not say. Ordered: the first match wins,
    // so "fell_back" is a warning even though the event also completed.
    private static readonly (string Word, string Result)[] ResultByTypeWord =
    [
        ("failed", "failed"),
        ("failure", "failed"),
        ("denied", "failed"),
        ("fell_back", "warning"),
        ("skipped", "skipped"),
        ("progress", "running"),
        ("started", "running"),
        ("succeeded", "success"),
        ("completed", "success"),
        ("passed_through", "success"),
        ("rejected", "success"),
        ("reported", "success"),
        ("cancelled", "success"),
    ];

    /// <summary>The facts for one event; a value the detail does not give, or gives invalidly, is <see langword="null"/>.</summary>
    public static ActivityFacts Classify(string eventType, string? detail)
    {
        var data = DetailDict(detail) ?? new WireObject();
        var type = eventType ?? string.Empty;

        var trigger = Member(data.Get("trigger"), Triggers);
        if (trigger is null && PersonStartedPrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal)))
        {
            // A sign-in, a password change or a repair someone clicked is, by definition, someone's action.
            trigger = "manual";
        }

        var result = Member(data.Get("result"), Results);
        if (result is null && data.Get("ok") is WireBool { Value: false })
        {
            result = "failed";
        }

        if (result is null)
        {
            var lowered = type.ToLowerInvariant();
            // An event type ends in its terminal verb, so a suffix match is checked first and wins:
            // "processing.failure_cleanup_sweep_completed" is a success, not a failure because it
            // contains "failure" (#540). Only when nothing is a suffix does the ordered substring scan run.
            result = ResultByTypeWord.FirstOrDefault(pair => lowered.EndsWith(pair.Word, StringComparison.Ordinal)).Result
                ?? ResultByTypeWord.FirstOrDefault(pair => lowered.Contains(pair.Word, StringComparison.Ordinal)).Result;
        }

        // A JSON integer only (not a boolean); a value beyond SQLite's INTEGER is left out.
        long? libraryId = data.Get("library_id") is WireInteger i && i.Value >= long.MinValue && i.Value <= long.MaxValue ? (long)i.Value : null;

        var relativePath = data.Get("relative_media_path") is WireString path ? RelativePathColumn(path.Value) : null;

        // A string or integer run_id only: booleans are rejected, as for library_id above, so
        // run_id: true never becomes the run key "run:True" (#540).
        string? runKey = null;
        if (data.Get("run_id") is (WireString or WireInteger) and var runId && WireStrings.Strip(WireConvert.Str(runId)).Length > 0)
        {
            runKey = WireStrings.Slice("run:" + WireConvert.Str(runId), 128);
        }

        return new ActivityFacts(trigger, result, libraryId, relativePath, runKey);
    }

    /// <summary>
    /// What the <c>relative_path</c> column holds for an event whose detail names <paramref name="relativeMediaPath"/>: the
    /// path stripped and capped at <see cref="RelativePathLimit"/> characters, or null when nothing is left. A lookup by
    /// that column goes through this too, so it finds exactly the rows the writer filled.
    /// </summary>
    public static string? RelativePathColumn(string relativeMediaPath)
    {
        ArgumentNullException.ThrowIfNull(relativeMediaPath);
        var stripped = WireStrings.Strip(relativeMediaPath);
        return stripped.Length > 0 ? WireStrings.Slice(stripped, RelativePathLimit) : null;
    }

    private static string? Member(WireValue? value, IReadOnlySet<string> allowed)
    {
        if (value is not WireString text)
        {
            return null;
        }

        var normalized = WireStrings.Strip(text.Value).ToLowerInvariant();
        return allowed.Contains(normalized) ? normalized : null;
    }

    /// <summary>The detail as a JSON object, or <see langword="null"/> when it is not one or does not parse.</summary>
    private static WireObject? DetailDict(string? detail)
    {
        var text = WireStrings.Strip(detail ?? string.Empty);
        if (!text.StartsWith('{'))
        {
            return null;
        }

        try
        {
            return WireJsonParser.Parse(text) as WireObject;
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }
    }
}
