using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>What a field that cannot be the same twice must still be.</summary>
internal enum Volatile
{
    /// <summary>A non-empty string, like the machine name and the version.</summary>
    Text,

    /// <summary>A UTC time as text, like <c>lastChangedUtc</c>.</summary>
    Moment,

    /// <summary>A whole number, like a row id.</summary>
    Number,
}

/// <summary>A field whose value changes from one recording to the next: it is checked for what it is, not what it was.</summary>
/// <param name="Path">Where it is, in <c>$.libraries[].id</c> form (an array position is written <c>[]</c>).</param>
/// <param name="Kind">What it must be.</param>
internal sealed record VolatileField(string Path, Volatile Kind);

/// <summary>
/// A deliberate change since the recording: at <paramref name="Path"/> Weir now answers <paramref name="Now"/> (or nothing, when
/// <paramref name="Removed"/>), for the reason in <paramref name="Why"/>. The recording is not edited. A pin that no longer applies,
/// because Weir answers what was recorded again, fails so it gets taken out.
/// </summary>
internal sealed record ExpectedDifference(string Issue, string Path, string Why, JsonNode? Now = null, bool Removed = false);

/// <summary>Compares the reply Deluno recorded with the reply Weir gave now, and says which field differs and where.</summary>
internal sealed partial class Comparison(IReadOnlyList<VolatileField> volatileFields, IReadOnlyList<ExpectedDifference> pins)
{
    private readonly List<string> _failures = [];
    private readonly HashSet<ExpectedDifference> _pinsUsed = [];

    /// <summary>The fields every recording shares that change each time.</summary>
    public static IReadOnlyList<VolatileField> Usual { get; } =
    [
        new("$.lastChangedUtc", Volatile.Moment),
        new("$.scheduledFor", Volatile.Moment),
        new("$.machine_name", Volatile.Text),
        new("$.version", Volatile.Text),
        new("$.libraries[].id", Volatile.Number),
    ];

    /// <summary>The failures of comparing the recorded body with the actual one, and of pins that no longer apply.</summary>
    public static IReadOnlyList<string> Differences(
        JsonNode? recorded, JsonNode? actual, IReadOnlyList<VolatileField> volatileFields, IReadOnlyList<ExpectedDifference> pins)
    {
        var comparison = new Comparison(volatileFields, pins);
        comparison.Compare("$", new Field(true, recorded), new Field(true, actual));
        foreach (var pin in pins.Where(pin => !comparison._pinsUsed.Contains(pin)))
        {
            comparison._failures.Add(
                $"{pin.Path}: the difference pinned for {pin.Issue} ({pin.Why}) did not happen, so the pin is stale and must be removed");
        }

        return comparison._failures;
    }

    private void Compare(string path, Field recorded, Field actual)
    {
        if (pins.FirstOrDefault(candidate => candidate.Path == path) is { } pin)
        {
            ComparePinned(path, pin, recorded, actual);
            return;
        }

        if (volatileFields.FirstOrDefault(candidate => candidate.Path == Generic(path)) is { } volatileField && recorded.Present && actual.Present)
        {
            CompareVolatile(path, volatileField.Kind, recorded.Node, actual.Node);
            return;
        }

        if (!recorded.Present || !actual.Present)
        {
            _failures.Add(!actual.Present
                ? $"{path}: recorded as {Show(recorded)}, but Weir's reply has no such field"
                : $"{path}: Weir's reply has {Show(actual)}, which the recording does not have");
            return;
        }

        switch (recorded.Node, actual.Node)
        {
            case (JsonObject recordedFields, JsonObject actualFields):
                foreach (var name in recordedFields.Select(field => field.Key).Union(actualFields.Select(field => field.Key)))
                {
                    Compare($"{path}.{name}", Of(recordedFields, name), Of(actualFields, name));
                }

                break;
            case (JsonArray recordedItems, JsonArray actualItems):
                for (var index = 0; index < Math.Max(recordedItems.Count, actualItems.Count); index++)
                {
                    Compare($"{path}[{index}]", At(recordedItems, index), At(actualItems, index));
                }

                break;
            default:
                if (!JsonNode.DeepEquals(recorded.Node, actual.Node))
                {
                    _failures.Add($"{path}: recorded {Show(recorded)}, Weir answered {Show(actual)}");
                }

                break;
        }
    }

    private void ComparePinned(string path, ExpectedDifference pin, Field recorded, Field actual)
    {
        var now = new Field(!pin.Removed, pin.Now);
        if (Same(recorded, actual))
        {
            return;
        }

        if (Same(now, actual))
        {
            _pinsUsed.Add(pin);
            return;
        }

        _failures.Add(
            $"{path}: recorded {Show(recorded)}; {pin.Issue} ({pin.Why}) makes it {Show(now)}; Weir answered {Show(actual)}");
    }

    private void CompareVolatile(string path, Volatile kind, JsonNode? recorded, JsonNode? actual)
    {
        if (recorded is null || actual is null)
        {
            if (recorded is not null || actual is not null)
            {
                _failures.Add($"{path}: recorded {Show(new Field(true, recorded))}, Weir answered {Show(new Field(true, actual))}; it changes each time, but whether it is null does not");
            }

            return;
        }

        var text = actual is JsonValue value && value.TryGetValue<string>(out var read) ? read : null;
        var problem = kind switch
        {
            Volatile.Text => string.IsNullOrWhiteSpace(text) ? "a non-empty text" : null,
            Volatile.Moment => text is not null && text.EndsWith('Z') && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
                ? null
                : "a UTC time written like 2026-10-11T01:59:07Z",
            _ => JsonFields.IsWholeNumber(actual) ? null : "a whole number",
        };
        if (problem is not null)
        {
            _failures.Add($"{path}: it changes each time, but it must be {problem}; Weir answered {Show(new Field(true, actual))}");
        }
    }

    private static bool Same(Field left, Field right) =>
        left.Present == right.Present && (!left.Present || JsonNode.DeepEquals(left.Node, right.Node));

    private static Field Of(JsonObject fields, string name) => new(fields.ContainsKey(name), fields[name]);

    private static Field At(JsonArray items, int index) => new(index < items.Count, index < items.Count ? items[index] : null);

    private static string Generic(string path) => ArrayPosition().Replace(path, "[]");

    private static string Show(Field field) => field.Present ? field.Node?.ToJsonString() ?? "null" : "nothing";

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex ArrayPosition();

    private readonly record struct Field(bool Present, JsonNode? Node);
}
