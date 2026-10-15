using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// One kind of download client: the user name it signs in with (none for SABnzbd and Deluge), which field of a connection holds its
/// secret, and whether it works without one.
/// </summary>
internal sealed record DownloadClientKind(string Name, string Product, string? Username, string SecretField, bool SecretRequired)
{
    public string SavedFlag => $"{SecretField}_is_saved";

    /// <summary>The words Weir answers with when it will not clear a secret the client cannot do without.</summary>
    public string RequiredMessage => Name == "sabnzbd"
        ? "SABnzbd needs an API key. Find it in SABnzbd under Config → General."
        : "Deluge needs its Web UI password.";
}

/// <summary>Steps on download client connections, all through the public API.</summary>
internal static class DownloadClients
{
    public const string Route = $"{WeirClient.Api}/download-clients/connections";

    public static readonly IReadOnlyList<DownloadClientKind> Kinds =
    [
        new("qbittorrent", "qBittorrent", "weir-qbit", "password", SecretRequired: false),
        new("sabnzbd", "SABnzbd", null, "api_key", SecretRequired: true),
        new("nzbget", "NZBGet", "weir-nzbget", "password", SecretRequired: false),
        new("transmission", "Transmission", "weir-transmission", "password", SecretRequired: false),
        new("deluge", "Deluge", null, "password", SecretRequired: true),
    ];

    /// <summary>Every kind's name, as the data of a theory.</summary>
    public static IEnumerable<object[]> KindNames() => Kinds.Select(kind => new object[] { kind.Name });

    /// <summary>The names of the kinds whose client works without a secret.</summary>
    public static IEnumerable<object[]> KindNamesWithoutRequiredSecret() =>
        Kinds.Where(kind => !kind.SecretRequired).Select(kind => new object[] { kind.Name });

    /// <summary>The names of the kinds whose client cannot do without its secret.</summary>
    public static IEnumerable<object[]> KindNamesWithRequiredSecret() =>
        Kinds.Where(kind => kind.SecretRequired).Select(kind => new object[] { kind.Name });

    public static DownloadClientKind Kind(string name) => Kinds.Single(kind => kind.Name == name);

    /// <summary>A fake client of this kind that asks for <paramref name="secret"/>.</summary>
    public static FakeDownloadClient StartFake(DownloadClientKind kind, string secret, string completedFolder = "/downloads/complete") =>
        FakeDownloadClient.Start(kind.Name, kind.Username, secret, completedFolder);

    /// <summary>Creates a connection to <paramref name="client"/> carrying <paramref name="secret"/> in the field its kind keeps it in.</summary>
    public static async Task<JsonObject> CreateAsync(WeirClient admin, DownloadClientKind kind, FakeDownloadClient client, string secret)
    {
        var body = new JsonObject { ["kind"] = kind.Name, ["base_url"] = client.BaseUrl, [kind.SecretField] = secret };
        if (kind.Username is not null)
        {
            body["username"] = kind.Username;
        }

        var created = await admin.PostWithCsrfAsync(Route, body);
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
        AssertNoSecret(created, secret);
        return created.Fields;
    }

    public static Task<WeirResponse> UpdateAsync(WeirClient admin, JsonObject connection, JsonObject changes) =>
        admin.PutWithCsrfAsync($"{Route}/{JsonFields.Id(connection)}", changes);

    /// <summary>The connection test, which signs in to the client with whatever Weir has saved.</summary>
    public static async Task<JsonObject> TestAsync(WeirClient admin, JsonObject connection)
    {
        var tested = await admin.PostWithCsrfAsync($"{Route}/{JsonFields.Id(connection)}/test");
        Assert.True(tested.Status == HttpStatusCode.OK, tested.ToString());
        return tested.Fields;
    }

    public static Task<WeirResponse> DeleteAsync(WeirClient admin, JsonObject connection) =>
        admin.DeleteWithCsrfBodyAsync($"{Route}/{JsonFields.Id(connection)}");

    public static async Task ClearAsync(WeirClient admin)
    {
        var listed = await admin.GetAsync(Route);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        foreach (var row in listed.Elements)
        {
            var deleted = await admin.DeleteWithCsrfBodyAsync($"{Route}/{JsonFields.Id(row!)}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
        }
    }

    /// <summary>A secret is stored write-only: nothing Weir answers carries it, and a field name that would hold it is not in the answer.</summary>
    public static void AssertNoSecret(WeirResponse response, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, response.Text);
        }

        foreach (var row in response.Json is JsonArray rows ? rows.Select(row => row!.AsObject()) : [response.Fields])
        {
            Assert.DoesNotContain("password", row.Select(field => field.Key));
            Assert.DoesNotContain("api_key", row.Select(field => field.Key));
        }
    }
}
