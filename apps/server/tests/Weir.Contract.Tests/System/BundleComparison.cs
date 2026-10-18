using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>Compares a section of one configuration backup with the same section of another, field by field.</summary>
internal static class BundleComparison
{
    /// <summary>The fields a restore stamps afresh, which a section's comparison leaves out; every other field must come back as it was.</summary>
    private static readonly string[] Stamps = ["updated_at", "product_display_name", "configuration_backup_last_run_at"];

    /// <summary>The fields an older release backed up that the current build no longer keeps (each workflow's weekly schedule decides when it works): a restore accepts and ignores them.</summary>
    private static readonly string[] Retired =
    [
        "movie_schedule_hours_limited", "movie_schedule_days", "movie_schedule_start", "movie_schedule_end",
        "tv_schedule_hours_limited", "tv_schedule_days", "tv_schedule_start", "tv_schedule_end",
    ];

    internal static JsonNode? Without(JsonNode? section)
    {
        var copy = section?.DeepClone();
        Strip(copy);
        return copy;
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject fields:
                foreach (var stamp in Stamps.Concat(Retired))
                {
                    fields.Remove(stamp);
                }

                foreach (var child in fields.Select(field => field.Value).ToList())
                {
                    Strip(child);
                }

                break;
            case JsonArray items:
                foreach (var child in items)
                {
                    Strip(child);
                }

                break;
        }
    }

    internal static void AssertSame(string section, JsonObject expected, JsonObject actual)
    {
        var differences = Differences(section, Without(expected[section]), Without(actual[section]));
        Assert.True(differences.Count == 0, $"{section} came back different:{Environment.NewLine}{string.Join(Environment.NewLine, differences)}");
    }

    /// <summary>Every field that is not the same in both, by its path, with what it was and what it is.</summary>
    internal static List<string> Differences(string path, JsonNode? was, JsonNode? now)
    {
        if (was is JsonObject wasFields && now is JsonObject nowFields)
        {
            return [.. wasFields.Select(field => field.Key).Union(nowFields.Select(field => field.Key)).SelectMany(name => Differences($"{path}.{name}", wasFields[name], nowFields[name]))];
        }

        if (was is JsonArray wasItems && now is JsonArray nowItems && wasItems.Count == nowItems.Count)
        {
            return [.. wasItems.SelectMany((item, index) => Differences($"{path}[{index}]", item, nowItems[index]))];
        }

        return JsonNode.DeepEquals(was, now) ? [] : [$"{path}: was {was?.ToJsonString() ?? "missing"}, is {now?.ToJsonString() ?? "missing"}"];
    }
}
