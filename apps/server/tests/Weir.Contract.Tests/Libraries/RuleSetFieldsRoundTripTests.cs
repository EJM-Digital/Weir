using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// Every setting of a rule set saves, reads back exactly as it was sent, and changes on its own. The list of settings is the one the
/// server documents (<c>/openapi.json</c>, the rule set it accepts), so a setting added later fails these tests until they know a value
/// for it.
/// </summary>
[ContractArea("libraries")]
public sealed class RuleSetFieldsRoundTripTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string RuleSets = $"{WeirClient.Api}/processing/rule-sets";

    private static readonly string[] NotSettings = ["csrf_token", "name"];

    private static readonly string[] OnlyInTheAnswer = ["id", "used_by_library_count", "updated_at"];

    private const string AudioOrder = "[{\"field\": \"channels\", \"value\": \">=5.1\"}, {\"field\": \"language\", \"value\": \"eng\"}]";
    private const string OtherAudioOrder = "[{\"field\": \"bitrate\"}, {\"field\": \"codec\", \"value\": \"dts\"}]";
    private const string SubtitleOrder = "[{\"field\": \"language\", \"value\": \"fre\"}, {\"field\": \"forced\"}]";
    private const string OtherSubtitleOrder = "[{\"field\": \"default\"}, {\"field\": \"language\", \"value\": \"spa\"}]";

    /// <summary>Values for the settings whose text means something: a first value, and a second one that differs from it.</summary>
    private static readonly Dictionary<string, (JsonNode First, JsonNode Second)> Texts = new()
    {
        ["primary_audio_lang"] = ("jpn", "kor"),
        ["secondary_audio_lang"] = ("fre", "ger"),
        ["tertiary_audio_lang"] = ("ger", "spa"),
        ["subtitle_langs_csv"] = ("eng,fre", "spa"),
        ["audio_sorters_json"] = (AudioOrder, OtherAudioOrder),
        ["subtitle_sorters_json"] = (SubtitleOrder, OtherSubtitleOrder),
        ["original_language_additional_csv"] = ("jpn,kor", "ita"),
        ["track_name_template"] = ("{language} {codec}", "{language} {channels}"),
        ["subtitle_max_per_language"] = (3, 5),
        ["track_name_overrides"] = (
            new JsonObject { ["forced"] = "{language} (Forced)", ["hearing_impaired"] = "{language} (SDH)", ["commentary"] = "{language} (Commentary)", ["audio_description"] = "{language} (AD)" },
            new JsonObject { ["forced"] = "{language} forced", ["hearing_impaired"] = "{language} sdh", ["commentary"] = "{language} comment", ["audio_description"] = "{language} ad" }),
    };

    /// <summary>The settings the server documents for a rule set, each with the schema that describes it.</summary>
    private static async Task<(JsonObject Accepted, JsonObject Answered)> DocumentedAsync(WeirServer server)
    {
        using var client = server.CreateClient();
        var document = await client.GetAsync("/openapi.json");
        Assert.True(document.Status == HttpStatusCode.OK, document.ToString());
        var schemas = document.Fields["components"]!["schemas"]!.AsObject();
        return (schemas["ProcessingRuleSetIn"]!["properties"]!.AsObject(), schemas["ProcessingRuleSetOut"]!["properties"]!.AsObject());
    }

    private static IEnumerable<string> SettingNames(JsonObject accepted) => accepted.Select(member => member.Key).Where(name => !NotSettings.Contains(name));

    /// <summary>A value that differs from the setting's default, in the form the setting takes.</summary>
    private static JsonNode First(string name, JsonObject schema) => Pick(name, schema, first: true);

    /// <summary>A value that differs from <see cref="First"/>.</summary>
    private static JsonNode Second(string name, JsonObject schema) => Pick(name, schema, first: false);

    private static JsonNode Pick(string name, JsonObject schema, bool first)
    {
        if (Texts.TryGetValue(name, out var texts))
        {
            return (first ? texts.First : texts.Second).DeepClone();
        }

        var kind = (string?)schema["type"];
        if (kind == "boolean")
        {
            var normally = (bool)schema["default"]!;
            return first ? !normally : normally;
        }

        if (schema["enum"] is JsonArray choices)
        {
            var normally = (string)schema["default"]!;
            var others = choices.Select(choice => (string)choice!).Where(choice => choice != normally).ToList();
            return first ? others[0] : others[1 % others.Count];
        }

        throw new InvalidOperationException($"The rule set takes a setting, {name}, that this test has no value for. Add one to {nameof(Texts)}.");
    }

    private static JsonObject Body(string name, IReadOnlyDictionary<string, JsonNode> settings)
    {
        var body = new JsonObject { ["name"] = name };
        foreach (var (setting, value) in settings)
        {
            body[setting] = value.DeepClone();
        }

        return body;
    }

    private static void AssertSame(JsonObject expected, JsonObject actual, string what)
    {
        foreach (var (setting, value) in expected)
        {
            Assert.True(Same(setting, value, actual[setting]), $"{what}: {setting} was sent as {value?.ToJsonString()} but read back as {actual[setting]?.ToJsonString()}");
        }
    }

    /// <summary>
    /// Equal, except that a track order is kept in the form Weir stores it: the same sorters in the same order, each with the
    /// reversed flag spelled out.
    /// </summary>
    private static bool Same(string setting, JsonNode? sent, JsonNode? read) =>
        setting.EndsWith("_sorters_json", StringComparison.Ordinal)
            ? Sorters((string?)sent) == Sorters((string?)read)
            : JsonNode.DeepEquals(sent, read);

    private static string Sorters(string? json) => string.Join(
        ";",
        JsonNode.Parse(string.IsNullOrEmpty(json) ? "[]" : json)!.AsArray().Select(sorter =>
            $"{(string?)sorter!["field"]}|{(string?)sorter["value"]}|{(bool?)sorter["reversed"] ?? false}"));

    [Fact]
    public async Task The_answer_names_every_setting_the_rule_set_accepts()
    {
        var (accepted, answered) = await DocumentedAsync(fixture.Server);

        var missing = SettingNames(accepted).Concat(["name"]).Where(name => !answered.ContainsKey(name)).ToList();
        Assert.True(missing.Count == 0, $"A rule set is saved with {string.Join(", ", missing)} but never read back.");
        Assert.All(OnlyInTheAnswer, name => Assert.True(answered.ContainsKey(name), name));
    }

    [Fact]
    public async Task A_rule_set_with_every_setting_changed_is_read_back_exactly()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var (accepted, _) = await DocumentedAsync(fixture.Server);
        var settings = SettingNames(accepted).ToDictionary(name => name, name => First(name, accepted[name]!.AsObject()));

        var made = await admin.PostWithCsrfAsync(RuleSets, Body("Every setting", settings));

        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());
        var sent = Body("Every setting", settings);
        AssertSame(sent, made.Fields, "the answer to the save");
        var listed = (await admin.GetAsync(RuleSets)).Elements.Single(item => (long)item!["id"]! == (long)made.Fields["id"]!)!.AsObject();
        AssertSame(sent, listed, "the list");
        Assert.Equal(0, (int)listed["used_by_library_count"]!);
    }

    [Fact]
    public async Task A_rule_set_saved_with_only_a_name_takes_the_default_the_server_documents_for_every_setting()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var (accepted, _) = await DocumentedAsync(fixture.Server);

        var made = await admin.PostWithCsrfAsync(RuleSets, new JsonObject { ["name"] = "Only a name" });

        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());
        foreach (var setting in SettingNames(accepted))
        {
            var documented = accepted[setting]!.AsObject()["default"];
            if (documented is not null && !setting.EndsWith("_sorters_json", StringComparison.Ordinal))
            {
                Assert.True(JsonNode.DeepEquals(documented, made.Fields[setting]), $"{setting} is documented to default to {documented.ToJsonString()} but saved as {made.Fields[setting]?.ToJsonString()}");
            }
        }
    }

    [Fact]
    public async Task Changing_one_setting_at_a_time_changes_only_that_setting_and_it_stays_changed()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var (accepted, _) = await DocumentedAsync(fixture.Server);
        var current = SettingNames(accepted).ToDictionary(name => name, name => First(name, accepted[name]!.AsObject()));
        var made = await admin.PostWithCsrfAsync(RuleSets, Body("One at a time", current));
        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());
        var path = $"{RuleSets}/{(long)made.Fields["id"]!}";

        foreach (var setting in current.Keys.ToList())
        {
            var before = Body("One at a time", current);
            current[setting] = Second(setting, accepted[setting]!.AsObject());

            var saved = await admin.PutWithCsrfAsync(path, Body("One at a time", current));

            Assert.True(saved.Status == HttpStatusCode.OK, $"{setting}: {saved}");
            AssertSame(Body("One at a time", current), saved.Fields, $"after changing {setting}");
            foreach (var (other, value) in before)
            {
                if (other != setting)
                {
                    Assert.True(Same(other, value, saved.Fields[other]), $"changing {setting} also changed {other}");
                }
            }

            var read = (await admin.GetAsync(RuleSets)).Elements.Single(item => (long)item!["id"]! == (long)made.Fields["id"]!)!.AsObject();
            AssertSame(Body("One at a time", current), read, $"the list after changing {setting}");
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteWithCsrfBodyAsync(path)).Status);
        Assert.DoesNotContain((await admin.GetAsync(RuleSets)).Elements, item => (long)item!["id"]! == (long)made.Fields["id"]!);
    }
}
