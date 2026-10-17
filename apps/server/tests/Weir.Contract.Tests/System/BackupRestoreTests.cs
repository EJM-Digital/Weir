using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>
/// Restore overwrites settings, so it must restore exactly. An install is set up with something other than the default in every
/// section the backup holds, backed up, changed all over, and restored from the backup; every section is then compared field by field
/// with how it was. Alerts and media managers are the exception the backup documents: it never carries their secrets, so a restore adds
/// the ones the install lacks, switched off and without an address, and leaves the ones it has exactly as they are.
/// </summary>
[ContractArea("system")]
public sealed class BackupRestoreTests
{
    private static readonly string[] Sections =
    [
        "suite_settings", "arr_library_operator_settings", "operator_settings", "rule_sets", "libraries", "media_manager_connections",
    ];

    /// <summary>The fields a restore stamps afresh, which a section's comparison leaves out; every other field must come back as it was.</summary>
    private static readonly string[] Stamps = ["updated_at", "product_display_name", "configuration_backup_last_run_at"];

    private static JsonNode? Without(JsonNode? section)
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
                foreach (var stamp in Stamps)
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

    private static void AssertSection(string section, JsonObject expected, JsonObject actual)
    {
        var want = Without(expected[section]);
        var got = Without(actual[section]);
        Assert.True(JsonNode.DeepEquals(want, got), $"{section} came back different.\nWas:  {want?.ToJsonString()}\nNow:  {got?.ToJsonString()}");
    }

    [Fact]
    public async Task Every_section_comes_back_exactly_as_it_was_backed_up()
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        var backup = await world.ExportAsync();
        Assert.Equal(2, backup["rule_sets"]!.AsArray().Count(rule => ((string)rule!["name"]!).StartsWith("Rules ", StringComparison.Ordinal)));

        await world.ChangeEverythingAsync();
        var changed = await world.ExportAsync();
        foreach (var section in Sections.Where(section => section is not ("arr_library_operator_settings" or "media_manager_connections")))
        {
            Assert.False(JsonNode.DeepEquals(Without(backup[section]), Without(changed[section])), $"{section} was not changed, so restoring it proves nothing.");
        }

        var restored = await world.RestoreAsync(backup);

        Assert.True(restored.Status == HttpStatusCode.OK, restored.ToString());
        var after = await world.ExportAsync();
        foreach (var section in Sections)
        {
            AssertSection(section, backup, after);
        }

        Assert.Equal(["roku", "apple_tv_4k"], SelectedDevices(await world.Admin.GetAsync($"{WeirClient.Api}/processing/direct-play/devices")));
    }

    [Fact]
    public async Task A_rule_set_comes_back_with_every_one_of_its_settings()
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        var backup = await world.ExportAsync();
        var before = (await world.Admin.GetAsync($"{WeirClient.Api}/processing/rule-sets")).Elements.Select(rule => rule!.AsObject()).OrderBy(rule => (long)rule["id"]!).ToList();
        await world.ChangeEverythingAsync();

        var restored = await world.RestoreAsync(backup);

        Assert.True(restored.Status == HttpStatusCode.OK, restored.ToString());
        var after = (await world.Admin.GetAsync($"{WeirClient.Api}/processing/rule-sets")).Elements.Select(rule => rule!.AsObject()).OrderBy(rule => (long)rule["id"]!).ToList();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            foreach (var (name, value) in before[i].Where(field => field.Key is not ("updated_at" or "used_by_library_count")))
            {
                Assert.True(JsonNode.DeepEquals(value, after[i][name]), $"{(string)before[i]["name"]!}: {name} was {value?.ToJsonString()} and is {after[i][name]?.ToJsonString()}");
            }
        }
    }

    [Fact]
    public async Task Alerts_and_connections_keep_their_secrets_because_a_backup_never_carried_them()
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        var backup = await world.ExportAsync();
        Assert.DoesNotContain("hooks.example.com", backup.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("discord.example.com", backup.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(BackupWorld.Secret, backup.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(world.Manager.ApiKey, backup.ToJsonString(), StringComparison.Ordinal);
        await world.ChangeEverythingAsync();

        var restored = await world.RestoreAsync(backup);

        Assert.True(restored.Status == HttpStatusCode.OK, restored.ToString());
        var alerts = await world.AlertsAsync();
        // The one the install still has is left as it is, address and all.
        Assert.Equal(("https://hooks.example.com/changed", false), ((string)alerts["Webhook alert"]["url"]!, (bool)alerts["Webhook alert"]["enabled"]!));
        Assert.Equal(["job_completed"], alerts["Webhook alert"]["events"]!.AsArray().Select(name => (string)name!));
        // The one it lost comes back as it was backed up, switched off and with no address to send to.
        Assert.Equal(("discord", string.Empty, false), ((string)alerts["Discord alert"]["provider"]!, (string)alerts["Discord alert"]["url"]!, (bool)alerts["Discord alert"]["enabled"]!));
        Assert.Equal(["processing_job_failed"], alerts["Discord alert"]["events"]!.AsArray().Select(name => (string)name!));
        // The connections were never touched, so the secrets saved before still sign in.
        Assert.Equal((true, true), await world.SecretsStillWorkAsync());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(999)]
    public async Task A_backup_made_by_a_version_this_one_cannot_restore_is_refused_with_a_plain_reason_and_changes_nothing(int format)
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        var backup = await world.ExportAsync();
        var before = await world.ExportAsync();
        var other = backup.DeepClone().AsObject();
        other["format_version"] = format;

        var refused = await world.RestoreAsync(other);

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("This backup was made by a version of Weir that this one cannot restore.", (string?)refused.Fields["detail"]);
        Assert.True(JsonNode.DeepEquals(before, await world.ExportAsync()), "a refused restore changed something");
    }

    [Theory]
    [InlineData("suite_settings")]
    [InlineData("arr_library_operator_settings")]
    [InlineData("operator_settings")]
    public async Task A_file_missing_a_whole_section_is_refused_with_a_plain_reason_and_changes_nothing(string section)
    {
        await using var world = await BackupWorld.StartAsync();
        await world.SetUpAsync();
        var before = await world.ExportAsync();
        var partial = before.DeepClone().AsObject();
        partial.Remove(section);

        var refused = await world.RestoreAsync(partial);

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("This file is not a complete Weir backup.", (string?)refused.Fields["detail"]);
        Assert.True(JsonNode.DeepEquals(before, await world.ExportAsync()), "a refused restore changed something");
    }

    private static string[] SelectedDevices(WeirResponse response) =>
        [.. response.Fields["devices"]!.AsArray().Where(device => (bool)device!["selected"]!).Select(device => (string)device!["id"]!)];
}
