using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>
/// A backup is kept for as long as a person keeps it, so one written by an earlier release is restored by whatever release they run
/// later. Each fixture is the backup a published release made after a short scripted session; it is restored on an install of the
/// current build, and every section it holds must be there afterwards.
/// </summary>
[ContractArea("system")]
public sealed class ReleaseBackupRestoreTests
{
    private static readonly string[] Sections =
    [
        "suite_settings", "arr_library_operator_settings", "operator_settings", "rule_sets", "libraries", "media_manager_connections",
    ];

    public static TheoryData<string> Versions() => ReleaseFixtures.Versions();

    private static async Task<(JsonObject Backup, JsonObject Restored)> RestoreAsync(string version)
    {
        await using var world = await BackupWorld.StartAsync();
        var backup = ReleaseFixtures.Backup(version);

        var restored = await world.RestoreAsync(backup);

        Assert.True(restored.Status == HttpStatusCode.OK, restored.ToString());
        return (backup, await world.ExportAsync());
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_backup_written_by_a_published_release_restores_every_section(string version)
    {
        var (backup, after) = await RestoreAsync(version);

        foreach (var section in Sections.Where(section => section != "media_manager_connections"))
        {
            BundleComparison.AssertSame(section, backup, after);
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task The_alert_and_the_media_manager_in_the_backup_come_back_switched_off_and_without_their_secrets(string version)
    {
        var (backup, after) = await RestoreAsync(version);

        var alert = Assert.Single(after["notification_channels"]!.AsArray())!.AsObject();
        var was = Assert.Single(backup["notification_channels"]!.AsArray())!.AsObject();
        Assert.Equal(((string)was["label"]!, (string)was["provider"]!, false), ((string)alert["label"]!, (string)alert["provider"]!, (bool)alert["enabled"]!));
        Assert.True(JsonNode.DeepEquals(was["events"], alert["events"]));
        var connection = Assert.Single(after["media_manager_connections"]!.AsArray())!.AsObject();
        var connectionWas = Assert.Single(backup["media_manager_connections"]!.AsArray())!.AsObject();
        Assert.False((bool)connection["enabled"]!);
        connection["enabled"] = connectionWas["enabled"]!.DeepClone();
        BundleComparison.AssertSame("media_manager_connections", new JsonObject { ["media_manager_connections"] = new JsonArray(connectionWas.DeepClone()) }, new JsonObject { ["media_manager_connections"] = new JsonArray(connection.DeepClone()) });
    }
}
