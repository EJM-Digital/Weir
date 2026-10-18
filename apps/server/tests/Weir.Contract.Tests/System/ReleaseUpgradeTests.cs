using System.Net;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>
/// A person upgrading from last week's release is the case that matters. Each fixture is the database a published release wrote after a
/// short scripted session; the current server is started on it as it would be after an update, and what the session left must be there.
/// </summary>
[ContractArea("system")]
public sealed class ReleaseUpgradeTests
{
    private const string Api = WeirClient.Api;

    public static TheoryData<string> Versions() => ReleaseFixtures.Versions();

    private static async Task<WeirServer> StartOnAsync(string version)
    {
        var server = await WeirServer.StartNewAsync();
        try
        {
            await server.StopAsync();
            ReleaseFixtures.PlaceDatabase(version, server);
            await server.RestartAsync();
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_server_started_on_the_database_of_a_published_release_upgrades_it_and_saves_a_copy_first(string version)
    {
        await using var server = await StartOnAsync(version);

        Assert.Contains("Before updating, Weir saved a copy of its data", server.LogText(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(server.Home, "backups", "pre-update"), "weir-*-to-*.db"));
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).Status);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task What_the_release_s_session_left_is_there_after_the_upgrade(string version)
    {
        await using var server = await StartOnAsync(version);
        using var admin = await server.CreateAdminClientAsync();

        var libraries = (await admin.GetAsync($"{Api}/processing/libraries")).Elements.Select(item => (string)item!["name"]!).Order(StringComparer.Ordinal);
        Assert.Equal(["Fixture Library", "Fixture Movies", "Movies", "TV"], libraries);
        Assert.Contains((await admin.GetAsync($"{Api}/processing/rule-sets")).Elements, rules => (string?)rules!["name"] == "Fixture rules");
        Assert.Equal(["Fixture alert"], (await admin.GetAsync($"{Api}/suite/notification-channels")).Fields["items"]!.AsArray().Select(item => (string)item!["label"]!));
        var devices = await admin.GetAsync($"{Api}/processing/direct-play/devices");
        Assert.Equal(["roku"], devices.Fields["devices"]!.AsArray().Where(device => (bool)device!["selected"]!).Select(device => (string)device!["id"]!));
        var files = (await admin.GetAsync($"{Api}/processing/files")).Fields["files"]!.AsArray().ToDictionary(file => (string)file!["relative_path"]!, file => (string)file!["status"]!);
        Assert.Equal("processed", files["Fixture.Film.2020/Fixture.Film.2020.mkv"]);
        Assert.Equal("rejected", files["Fixture.Silent.2021/Fixture.Silent.2021.mkv"]);
        var activity = (await admin.GetAsync($"{Api}/activity/recent")).Fields["items"]!.AsArray().Select(item => (string)item!["title"]!).ToList();
        Assert.Contains(activity, title => title.Contains("Fixture.Film.2020.mkv was processed successfully", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Starting_the_upgraded_server_again_changes_nothing(string version)
    {
        await using var server = await StartOnAsync(version);
        string Revision(StoppedDatabase stopped) => (string)SeedSql.Scalar(stopped.Connection, "SELECT revision FROM schema_version")!;
        string head;
        await using (var first = await server.StopForDatabaseAsync(restart: false))
        {
            head = Revision(first);
        }

        await server.RestartAsync();

        await using var second = await server.StopForDatabaseAsync(restart: false);
        Assert.Equal(head, Revision(second));
        Assert.Single(Directory.GetFiles(Path.Combine(server.Home, "backups", "pre-update"), "weir-*-to-*.db"));
    }
}
