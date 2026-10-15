using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// Every field of a download client connection, for each of the five kinds: what creating one with it returns, and that updating
/// it changes it and nothing else. Passwords and API keys are write-only, so their field is read back as "saved or not".
/// </summary>
[ContractArea("media_managers")]
public sealed class DownloadClientFieldsTests(DownloadClientServerFixture fixture) : IClassFixture<DownloadClientServerFixture>
{
    private const string Address = "http://192.0.2.50:8080";
    private const string OtherAddress = "http://192.0.2.51:9090";
    private static readonly string[] SecretsUsed = ["base-pass", "base-key", "other-pass", "other-key"];

    /// <summary>The fields an update can change: how each is sent, what to change it to, what clears it, and what the answer calls it.</summary>
    private sealed record Field(string Name, Func<JsonNode> Changed, Func<JsonNode>? Cleared, string Shown);

    private static readonly IReadOnlyDictionary<string, Field> Fields = new[]
    {
        new Field("base_url", () => OtherAddress, null, "base_url"),
        new Field("enabled", () => false, () => true, "enabled"),
        new Field("username", () => "other-user", () => string.Empty, "username"),
        new Field("password", () => "other-pass", () => string.Empty, "password_is_saved"),
        new Field("api_key", () => "other-key", () => string.Empty, "api_key_is_saved"),
        new Field("nickname", () => "Other", () => string.Empty, "nickname"),
    }.ToDictionary(field => field.Name);

    public static IEnumerable<object[]> KindsAndFields() =>
        DownloadClients.Kinds.SelectMany(kind => Fields.Keys.Select(field => new object[] { kind.Name, field }));

    /// <summary>The fields an update can empty: not the address, and not a secret its client cannot work without.</summary>
    public static IEnumerable<object[]> KindsAndClearableFields() =>
        DownloadClients.Kinds.SelectMany(kind => Fields.Values
            .Where(field => field.Cleared is not null && !(kind.SecretRequired && field.Name == kind.SecretField))
            .Select(field => new object[] { kind.Name, field.Name }));

    private async Task<WeirClient> OperatorAsync()
    {
        var admin = await fixture.Server.CreateAdminClientAsync();
        await DownloadClients.ClearAsync(admin);
        return admin;
    }

    private static JsonObject EveryField(DownloadClientKind kind) => new()
    {
        ["kind"] = kind.Name,
        ["enabled"] = false,
        ["base_url"] = Address,
        ["username"] = "base-user",
        ["password"] = "base-pass",
        ["api_key"] = "base-key",
        ["nickname"] = "Base",
    };

    private static async Task<JsonObject> CreateWithEveryFieldAsync(WeirClient admin, DownloadClientKind kind)
    {
        var created = await admin.PostWithCsrfAsync(DownloadClients.Route, EveryField(kind));
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        DownloadClients.AssertNoSecret(created, SecretsUsed);
        return created.Fields;
    }

    /// <summary>The answer without what changes on its own: the name Weir derives from the address, and what the last test or call recorded.</summary>
    private static JsonObject Stable(JsonObject row, params string[] alsoLeftOut)
    {
        var copy = row.DeepClone().AsObject();
        foreach (var name in new[] { "name", "last_test_ok", "last_test_at", "last_test_detail", "last_answer_ms", "last_used_at" }.Concat(alsoLeftOut))
        {
            copy.Remove(name);
        }

        return copy;
    }

    [Theory]
    [MemberData(nameof(DownloadClients.KindNames), MemberType = typeof(DownloadClients))]
    public async Task Creating_a_connection_returns_every_field_it_was_given(string name)
    {
        var kind = DownloadClients.Kind(name);
        using var admin = await OperatorAsync();

        var row = await CreateWithEveryFieldAsync(admin, kind);

        Assert.Equal(name, (string)row["kind"]!);
        Assert.Equal($"{kind.Product} on 192.0.2.50", (string)row["name"]!);
        Assert.False((bool)row["enabled"]!);
        Assert.Equal(Address, (string)row["base_url"]!);
        Assert.Equal("base-user", (string)row["username"]!);
        Assert.True((bool)row["password_is_saved"]!);
        Assert.True((bool)row["api_key_is_saved"]!);
        Assert.Equal("Base", (string)row["nickname"]!);
        JsonFields.AssertNull(row, "last_test_ok");
        JsonFields.AssertNull(row, "last_test_at");
        JsonFields.AssertNull(row, "last_test_detail");
        var listed = Assert.Single((await admin.GetAsync(DownloadClients.Route)).Elements)!;
        Assert.Equal(row.ToJsonString(), listed.ToJsonString());
        Assert.Equal(row.ToJsonString(), (await admin.GetAsync($"{DownloadClients.Route}/{JsonFields.Id(row)}")).Json.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(DownloadClients.KindNamesWithoutRequiredSecret), MemberType = typeof(DownloadClients))]
    public async Task A_connection_given_only_its_kind_starts_enabled_with_no_address_and_nothing_saved(string name)
    {
        using var admin = await OperatorAsync();

        var created = await admin.PostWithCsrfAsync(DownloadClients.Route, new JsonObject { ["kind"] = name });

        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        var row = created.Fields;
        Assert.True((bool)row["enabled"]!);
        Assert.Equal(string.Empty, (string)row["base_url"]!);
        JsonFields.AssertNull(row, "username");
        JsonFields.AssertNull(row, "nickname");
        Assert.False((bool)row["password_is_saved"]!);
        Assert.False((bool)row["api_key_is_saved"]!);
    }

    [Theory]
    [MemberData(nameof(DownloadClients.KindNamesWithRequiredSecret), MemberType = typeof(DownloadClients))]
    public async Task A_connection_cannot_be_created_without_the_secret_its_client_cannot_work_without(string name)
    {
        var kind = DownloadClients.Kind(name);
        using var admin = await OperatorAsync();

        var refused = await admin.PostWithCsrfAsync(DownloadClients.Route, new JsonObject { ["kind"] = name, ["base_url"] = Address });

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal(kind.RequiredMessage, (string)refused.Fields["detail"]!);
        Assert.Empty((await admin.GetAsync(DownloadClients.Route)).Elements);
    }

    [Theory]
    [MemberData(nameof(KindsAndFields))]
    public async Task Updating_one_field_changes_that_field_and_nothing_else(string kindName, string fieldName)
    {
        var kind = DownloadClients.Kind(kindName);
        var field = Fields[fieldName];
        using var admin = await OperatorAsync();
        var created = await CreateWithEveryFieldAsync(admin, kind);

        var updated = await DownloadClients.UpdateAsync(admin, created, new JsonObject { [fieldName] = field.Changed() });

        Assert.True(updated.Status == HttpStatusCode.OK, updated.ToString());
        DownloadClients.AssertNoSecret(updated, SecretsUsed);
        var expected = fieldName is "password" or "api_key" ? JsonValue.Create(true) : field.Changed();
        Assert.Equal(expected.ToJsonString(), updated.Fields[field.Shown]!.ToJsonString());
        Assert.Equal(Stable(created, field.Shown).ToJsonString(), Stable(updated.Fields, field.Shown).ToJsonString());
        Assert.Equal(
            fieldName == "base_url" ? $"{kind.Product} on 192.0.2.51" : $"{kind.Product} on 192.0.2.50",
            (string)updated.Fields["name"]!);
        Assert.Equal(updated.Text, (await admin.GetAsync($"{DownloadClients.Route}/{JsonFields.Id(created)}")).Text);
        Assert.Equal(updated.Fields.ToJsonString(), Assert.Single((await admin.GetAsync(DownloadClients.Route)).Elements)!.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(KindsAndClearableFields))]
    public async Task Clearing_one_field_empties_that_field_and_nothing_else(string kindName, string fieldName)
    {
        var kind = DownloadClients.Kind(kindName);
        var field = Fields[fieldName];
        using var admin = await OperatorAsync();
        var created = await CreateWithEveryFieldAsync(admin, kind);

        var cleared = await DownloadClients.UpdateAsync(admin, created, new JsonObject { [fieldName] = field.Cleared!() });

        Assert.True(cleared.Status == HttpStatusCode.OK, cleared.ToString());
        var expected = fieldName switch
        {
            "password" or "api_key" => JsonValue.Create(false),
            "enabled" => JsonValue.Create(true),
            _ => null,
        };
        Assert.Equal(expected?.ToJsonString() ?? "null", cleared.Fields[field.Shown]?.ToJsonString() ?? "null");
        Assert.Equal(Stable(created, field.Shown).ToJsonString(), Stable(cleared.Fields, field.Shown).ToJsonString());
    }

    [Fact]
    public async Task A_connection_with_no_address_says_so_when_tested()
    {
        using var admin = await OperatorAsync();
        var created = await admin.PostWithCsrfAsync(DownloadClients.Route, new JsonObject { ["kind"] = "qbittorrent" });

        var tested = await DownloadClients.TestAsync(admin, created.Fields);

        Assert.False((bool)tested["ok"]!);
        Assert.Equal("Add the address where this app can be reached, then test again.", (string)tested["detail"]!);
    }

    [Fact]
    public async Task An_address_that_will_not_work_is_refused_on_create_and_on_update_and_nothing_changes()
    {
        using var admin = await OperatorAsync();
        var created = await CreateWithEveryFieldAsync(admin, DownloadClients.Kind("qbittorrent"));

        var onCreate = await admin.PostWithCsrfAsync(DownloadClients.Route, new JsonObject { ["kind"] = "qbittorrent", ["base_url"] = "ftp://192.0.2.50/files" });
        var onUpdate = await DownloadClients.UpdateAsync(admin, created, new JsonObject { ["base_url"] = "ftp://192.0.2.50/files" });

        Assert.Equal(HttpStatusCode.BadRequest, onCreate.Status);
        Assert.Equal(HttpStatusCode.BadRequest, onUpdate.Status);
        Assert.StartsWith("That address will not work: ", (string)onUpdate.Fields["detail"]!);
        Assert.Equal(created.ToJsonString(), Assert.Single((await admin.GetAsync(DownloadClients.Route)).Elements)!.ToJsonString());
    }
}
