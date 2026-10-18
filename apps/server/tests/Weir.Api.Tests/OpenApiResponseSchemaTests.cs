using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weir.Api.Tests.Platform;

namespace Weir.Api.Tests;

/// <summary>
/// The web app is generated from <c>GET /openapi.json</c>, so a response that does not look like its schema is a screen reading a field
/// that is not there. Every route that can be asked with nothing but a signed-in account is asked on a running server, and the body
/// it answers is checked against the schema the document gives it: each declared field has the declared type, each required field is sent,
/// and a field that may be empty is allowed to be.
/// </summary>
public sealed class OpenApiResponseSchemaTests
{
    /// <summary>Fewest routes that must be checked: routes that need a state this test does not build answer something else, and a route ending in <c>/stream</c> keeps its response open, so neither is read.</summary>
    private const int FewestRoutesChecked = 40;

    [Fact]
    public async Task Every_body_a_route_answers_on_a_fresh_server_matches_the_schema_the_document_gives_it()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var admin = new ApiTestClient(server);
        await admin.SignInAsync();
        var document = (JsonObject)(await ApiTestClient.Json(await server.Client.GetAsync("/openapi.json")));
        var checkedRoutes = new List<string>();
        var problems = new List<string>();

        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            if (path.Contains('{', StringComparison.Ordinal) || path.EndsWith("/stream", StringComparison.Ordinal) || item!["get"] is not JsonObject get || AsksForQuery(get))
            {
                continue;
            }

            if (get["responses"]?["200"]?["content"]?["application/json"]?["schema"] is not JsonNode schema)
            {
                continue;
            }

            using var response = await admin.GetAsync(path);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json")
            {
                continue;
            }

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            checkedRoutes.Add(path);
            problems.AddRange(Check(document, schema, body, "GET " + path));
        }

        Assert.True(checkedRoutes.Count >= FewestRoutesChecked, $"Only {checkedRoutes.Count} routes could be checked: {string.Join(", ", checkedRoutes)}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static bool AsksForQuery(JsonObject operation) =>
        operation["parameters"] is JsonArray parameters && parameters.OfType<JsonObject>().Any(parameter => parameter["required"]?.GetValue<bool>() == true);

    private static JsonNode Resolve(JsonObject document, JsonNode schema)
    {
        while (schema is JsonObject { } node && node["$ref"] is JsonValue reference)
        {
            var name = reference.GetValue<string>()["#/components/schemas/".Length..];
            schema = document["components"]!["schemas"]![name]!;
        }

        return schema;
    }

    /// <summary>What is wrong with <paramref name="value"/> as a <paramref name="schema"/>, each told where in the body it is.</summary>
    private static List<string> Check(JsonObject document, JsonNode schema, JsonNode? value, string where)
    {
        if (Resolve(document, schema) is not JsonObject node)
        {
            return [];
        }

        foreach (var alternatives in new[] { "anyOf", "oneOf" })
        {
            if (node[alternatives] is JsonArray options)
            {
                var attempts = options.Select(option => Check(document, option!, value, where)).ToList();
                return attempts.Any(attempt => attempt.Count == 0) ? [] : [$"{where}: {Describe(value)} is none of: {string.Join(" | ", options.Select(option => Resolve(document, option!)["type"]?.ToJsonString() ?? "object"))}"];
            }
        }

        if (node["allOf"] is JsonArray all)
        {
            return [.. all.SelectMany(part => Check(document, part!, value, where))];
        }

        if (node["const"] is JsonNode constant)
        {
            return JsonNode.DeepEquals(constant, value) ? [] : [$"{where}: {Describe(value)} is not {constant.ToJsonString()}"];
        }

        if (node["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(choice, value)))
        {
            return [$"{where}: {Describe(value)} is not one of {choices.ToJsonString()}"];
        }

        var type = node["type"]?.GetValue<string>();
        return type switch
        {
            "null" => value is null ? [] : [$"{where}: {Describe(value)} is not null"],
            "string" => IsString(value, node["format"]?.GetValue<string>()) ? [] : [$"{where}: {Describe(value)} is not a {node["format"]?.GetValue<string>() ?? "string"}"],
            "integer" => IsNumber(value, integer: true) ? [] : [$"{where}: {Describe(value)} is not an integer"],
            "number" => IsNumber(value, integer: false) ? [] : [$"{where}: {Describe(value)} is not a number"],
            "boolean" => value is JsonValue boolean && boolean.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? [] : [$"{where}: {Describe(value)} is not true or false"],
            "array" => CheckArray(document, node, value, where),
            "object" => CheckObject(document, node, value, where),
            _ => type is null && node["properties"] is not null ? CheckObject(document, node, value, where) : [],
        };
    }

    private static List<string> CheckArray(JsonObject document, JsonObject node, JsonNode? value, string where)
    {
        if (value is not JsonArray array)
        {
            return [$"{where}: {Describe(value)} is not a list"];
        }

        return node["items"] is JsonNode items ? [.. array.SelectMany((item, index) => Check(document, items, item, $"{where}[{index.ToString(CultureInfo.InvariantCulture)}]"))] : [];
    }

    private static List<string> CheckObject(JsonObject document, JsonObject node, JsonNode? value, string where)
    {
        if (value is not JsonObject body)
        {
            return [$"{where}: {Describe(value)} is not an object"];
        }

        var problems = new List<string>();
        var properties = node["properties"] as JsonObject;
        foreach (var required in node["required"] as JsonArray ?? [])
        {
            if (!body.ContainsKey(required!.GetValue<string>()))
            {
                problems.Add($"{where}: the field '{required.GetValue<string>()}' is required and was not sent");
            }
        }

        foreach (var (name, member) in body)
        {
            if (properties?[name] is JsonNode declared)
            {
                problems.AddRange(Check(document, declared, member, $"{where}.{name}"));
            }
            else if (node["additionalProperties"] is JsonObject extra)
            {
                problems.AddRange(Check(document, extra, member, $"{where}.{name}"));
            }
        }

        return problems;
    }

    private static bool IsString(JsonNode? value, string? format) =>
        value is JsonValue text && text.GetValueKind() == JsonValueKind.String &&
        (format != "date-time" || DateTimeOffset.TryParse(text.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _));

    private static bool IsNumber(JsonNode? value, bool integer) =>
        value is JsonValue number && number.GetValueKind() == JsonValueKind.Number &&
        (!integer || number.ToJsonString().IndexOfAny(['.', 'e', 'E']) < 0);

    private static string Describe(JsonNode? value)
    {
        var text = value?.ToJsonString() ?? "null";
        return text.Length > 60 ? text[..60] + "…" : text;
    }
}
