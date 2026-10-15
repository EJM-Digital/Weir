using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// Every operator setting the API documents, taken from the server's own OpenAPI schema: each saves through <c>PUT</c> and reads
/// back, the range the schema gives is the range the server enforces, and a setting added to the schema that these tests cannot
/// exercise fails them until it is taught.
/// </summary>
[ContractArea("libraries")]
public sealed class ProcessingOperatorSettingsRoundTripTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Path = Api + "/processing/operator-settings";
    private const string SettingsSchema = "ProcessingOperatorSettingsPutIn";
    private const string AnswerSchema = "ProcessingOperatorSettingsOut";
    private const string TokenField = "csrf_token";
    private const string UpdatedAtField = "updated_at";

    /// <summary>What the answer carries besides the settings a person saves.</summary>
    private static readonly string[] AnswerOnlyFields = ["schedule_timezone", UpdatedAtField];

    private sealed record Setting(string Name, string Type, long? Minimum, long? Maximum)
    {
        public bool IsInteger => Type == "integer";
    }

    private async Task<(JsonObject Settings, JsonObject Answer)> SchemasAsync()
    {
        using var anonymous = fixture.Server.CreateClient();
        var document = await anonymous.GetAsync("/openapi.json");
        document.ShouldBe(HttpStatusCode.OK);
        var schemas = document.Fields["components"]!["schemas"]!.AsObject();
        return (schemas[SettingsSchema]!["properties"]!.AsObject(), schemas[AnswerSchema]!["properties"]!.AsObject());
    }

    private async Task<IReadOnlyList<Setting>> SettingsAsync()
    {
        var (settings, _) = await SchemasAsync();
        return settings
            .Where(property => property.Key != TokenField)
            .Select(property => Describe(property.Key, property.Value!.AsObject()))
            .ToList();
    }

    /// <summary>A setting is optional in the body, so its schema is the value's schema or null; the value's part is what is read.</summary>
    private static Setting Describe(string name, JsonObject schema)
    {
        var value = schema["anyOf"]?.AsArray().Select(alternative => alternative!.AsObject()).Single(alternative => (string?)alternative["type"] != "null") ?? schema;
        var type = (string?)value["type"];
        Assert.True(type is "integer" or "boolean", $"'{name}' is documented as {value.ToJsonString()}; teach this test how to save that kind of setting.");
        if (type == "boolean")
        {
            return new Setting(name, type, null, null);
        }

        var minimum = WholeNumber(value["minimum"]);
        var maximum = WholeNumber(value["maximum"]);
        Assert.True(minimum is not null && maximum is not null, $"'{name}' is documented without both ends of its range: {value.ToJsonString()}");
        return new Setting(name, type, minimum, maximum);
    }

    /// <summary>A limit as the schema writes it, which may be written with a fraction or an exponent.</summary>
    private static long? WholeNumber(JsonNode? limit) => limit is null ? null : checked((long)(double)limit);

    /// <summary>The ends of an integer setting's range and the middle, or both answers for a switch.</summary>
    private static IEnumerable<JsonNode> ValidValues(Setting setting)
    {
        if (!setting.IsInteger)
        {
            return [JsonValue.Create(true), JsonValue.Create(false)];
        }

        long[] values = [setting.Minimum!.Value, setting.Maximum!.Value, (setting.Minimum.Value + setting.Maximum.Value) / 2];
        return values.Distinct().Select(value => (JsonNode)JsonValue.Create(value));
    }

    private async Task<JsonObject> CurrentAsync(WeirClient admin)
    {
        var response = await admin.GetAsync(Path);
        response.ShouldBe(HttpStatusCode.OK);
        return response.Fields;
    }

    private static void AssertOnlyChanged(string setting, JsonObject before, JsonObject after)
    {
        foreach (var (name, value) in before.Where(pair => pair.Key != setting && pair.Key != UpdatedAtField))
        {
            Assert.True(
                value?.ToJsonString() == after[name]?.ToJsonString(),
                $"Saving '{setting}' changed '{name}' from {value?.ToJsonString()} to {after[name]?.ToJsonString()}.");
        }
    }

    [Fact]
    public async Task The_schema_lists_every_setting_the_answer_carries_and_no_other()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var (settings, answer) = await SchemasAsync();
        var current = await CurrentAsync(admin);

        var saved = settings.Select(property => property.Key).Where(name => name != TokenField).Order(StringComparer.Ordinal).ToList();
        var documentedAnswer = answer.Select(property => property.Key).Order(StringComparer.Ordinal).ToList();
        var returned = current.Select(property => property.Key).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(documentedAnswer, returned);
        Assert.Equal(documentedAnswer.Except(AnswerOnlyFields).Order(StringComparer.Ordinal).ToList(), saved);
    }

    [Fact]
    public async Task Every_setting_saves_and_reads_back_without_changing_another()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        foreach (var setting in await SettingsAsync())
        {
            foreach (var value in ValidValues(setting))
            {
                var before = await CurrentAsync(admin);

                var put = await admin.PutWithCsrfAsync(Path, Obj((setting.Name, value.DeepClone())));

                Assert.True(put.Status == HttpStatusCode.OK, $"Saving {setting.Name} = {value.ToJsonString()}: {put}");
                var after = await CurrentAsync(admin);
                Assert.True(
                    value.ToJsonString() == put.Fields[setting.Name]?.ToJsonString() && value.ToJsonString() == after[setting.Name]?.ToJsonString(),
                    $"Saved {setting.Name} = {value.ToJsonString()}; the answer says {put.Fields[setting.Name]?.ToJsonString()} and a later read says {after[setting.Name]?.ToJsonString()}.");
                Assert.Equal(after.ToJsonString(), put.Fields.ToJsonString());
                AssertOnlyChanged(setting.Name, before, after);
            }
        }
    }

    [Fact]
    public async Task A_number_outside_the_documented_range_is_refused_in_the_documented_words_and_nothing_is_saved()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        foreach (var setting in (await SettingsAsync()).Where(setting => setting.IsInteger))
        {
            var below = (Value: setting.Minimum!.Value - 1, Type: "greater_than_equal", Words: $"Input should be greater than or equal to {setting.Minimum}");
            var above = (Value: setting.Maximum!.Value + 1, Type: "less_than_equal", Words: $"Input should be less than or equal to {setting.Maximum}");
            foreach (var (value, type, words) in new[] { below, above })
            {
                var before = await CurrentAsync(admin);

                var put = await admin.PutWithCsrfAsync(Path, Obj((setting.Name, value)));

                Assert.True(put.Status == HttpStatusCode.UnprocessableEntity, $"Saving {setting.Name} = {value} was not refused: {put}");
                var problems = put.Fields["detail"]!.AsArray();
                var problem = Assert.Single(problems)!;
                Assert.Equal(type, (string?)problem["type"]);
                Assert.Equal(["body", setting.Name], StringList(problem["loc"]));
                Assert.Equal(words, (string?)problem["msg"]);
                Assert.Equal(before.ToJsonString(), (await CurrentAsync(admin)).ToJsonString());
            }
        }
    }

    [Fact]
    public async Task A_value_of_the_wrong_kind_is_refused_and_nothing_is_saved()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        foreach (var setting in await SettingsAsync())
        {
            var (wrong, type) = setting.IsInteger
                ? ((JsonNode)JsonValue.Create("often"), "int_parsing")
                : (JsonValue.Create("perhaps"), "bool_parsing");
            var before = await CurrentAsync(admin);

            var put = await admin.PutWithCsrfAsync(Path, Obj((setting.Name, wrong)));

            Assert.True(put.Status == HttpStatusCode.UnprocessableEntity, $"Saving {setting.Name} = {wrong.ToJsonString()} was not refused: {put}");
            var problem = Assert.Single(put.Fields["detail"]!.AsArray())!;
            Assert.Equal(type, (string?)problem["type"]);
            Assert.Equal(["body", setting.Name], StringList(problem["loc"]));
            Assert.Equal(before.ToJsonString(), (await CurrentAsync(admin)).ToJsonString());
        }
    }

    [Fact]
    public async Task A_setting_sent_as_null_is_left_as_it_was()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        foreach (var setting in await SettingsAsync())
        {
            var before = await CurrentAsync(admin);
            var anotherSetting = setting.Name == "keep_failed_work_files" ? "movie_schedule_enabled" : "keep_failed_work_files";

            var put = await admin.PutWithCsrfAsync(Path, Obj((setting.Name, null), (anotherSetting, before[anotherSetting]!.DeepClone())));

            Assert.True(put.Status == HttpStatusCode.OK, $"Sending {setting.Name} as null: {put}");
            var after = await CurrentAsync(admin);
            Assert.Equal(before[setting.Name]?.ToJsonString(), after[setting.Name]?.ToJsonString());
        }
    }
}
