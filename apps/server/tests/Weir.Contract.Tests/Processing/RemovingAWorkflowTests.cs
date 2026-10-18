using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// Removing a workflow removes Weir's records of it and nothing a person owns: every folder and file it worked on stays exactly where it
/// was, the media manager it was linked to stays, another workflow and the profile they share are untouched, and no job is left to run
/// for a workflow that no longer exists.
/// </summary>
[ContractArea("processing")]
public sealed class RemovingAWorkflowTests
{
    private const string Api = WeirClient.Api;
    private const string Film = "Linked.Film.2024/film.mkv";
    private const string OtherFilm = "Other.Film.2024/film.mkv";

    private static async Task<JsonObject> ScannedAsync(Scenario scenario, JsonObject library, string relativePath)
    {
        await scenario.WaitForJobFinishedAsync(await scenario.EnqueueScanAsync(library, enqueueRemuxJobs: false));
        return await Poll.UntilAsync(
            async () => await scenario.FileRowAsync(library, relativePath) is { } row && row["size_bytes"]!.GetValue<long>() > 0 ? row : null,
            $"a scan to record {relativePath}");
    }

    /// <summary>Every file under the scenario's folder with its size, for comparing before and after.</summary>
    private static Dictionary<string, long> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), path => new FileInfo(path).Length, StringComparer.Ordinal);

    [Fact]
    public async Task Removing_a_workflow_with_files_a_schedule_and_a_link_leaves_every_folder_and_the_rest_of_Weir_alone()
    {
        // The scheduler looks at every workflow once it is saved and queues a periodic scan that lands whenever its tick does, which on a slow
        // machine is after the test has waited for its own scans and before it removes the workflow. The test queues the scans it needs.
        await using var scenario = await Scenario.StartAsync(("WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_SCHEDULE_ENABLED", "0"));
        var media = FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"]));
        var (_, linked) = await scenario.RadarrSetupAsync();
        var otherWatched = Directory.CreateDirectory(Path.Combine(scenario.Root, "other-watched")).FullName;
        var other = await scenario.CreateLibraryAsync(
            ("name", "Other Movies"), ("watched_folder", otherWatched),
            ("work_folder", Directory.CreateDirectory(Path.Combine(scenario.Root, "other-work")).FullName),
            ("output_folder", Directory.CreateDirectory(Path.Combine(scenario.Root, "other-output")).FullName));
        scenario.WriteRelease("Linked.Film.2024", "film.mkv", media);
        Directory.CreateDirectory(Path.Combine(otherWatched, "Other.Film.2024"));
        await File.WriteAllBytesAsync(Path.Combine(otherWatched, OtherFilm), media);
        await File.WriteAllTextAsync(Path.Combine(scenario.Folders.Work, "half-finished.tmp"), "work in progress");
        await File.WriteAllTextAsync(Path.Combine(scenario.Folders.Output, "delivered.mkv"), "delivered");
        await ScannedAsync(scenario, linked, Film);
        await ScannedAsync(scenario, other, OtherFilm);
        var scheduled = await scenario.Admin.PostWithCsrfAsync($"{Api}/processing/libraries/{(int)linked["id"]!}/library-schedule", new JsonObject { ["enabled"] = true });
        Assert.True(scheduled.Status == HttpStatusCode.OK, scheduled.ToString());
        var connectionId = (int)linked["manager_connection_ids"]![0]!;
        var profile = (int)linked["rule_set_id"]!;
        var usersBefore = await ProfileUsersAsync(scenario, profile);
        var before = Tree(scenario.Root);
        var workflowsBefore = (await scenario.Admin.GetAsync($"{Api}/processing/libraries")).Elements.Select(row => (int)row!["id"]!).ToList();

        var removed = await scenario.Admin.DeleteWithCsrfBodyAsync($"{Api}/processing/libraries/{(int)linked["id"]!}");

        Assert.True(removed.Status == HttpStatusCode.NoContent, removed.ToString());
        Assert.Equal(before, Tree(scenario.Root));
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.Admin.GetAsync($"{Api}/processing/libraries/{(int)linked["id"]!}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.Admin.GetAsync($"{Api}/processing/libraries/{(int)linked["id"]!}/library-settings")).Status);
        Assert.Equal(
            workflowsBefore.Where(id => id != (int)linked["id"]!),
            (await scenario.Admin.GetAsync($"{Api}/processing/libraries")).Elements.Select(row => (int)row!["id"]!));
        Assert.NotNull(await scenario.FileRowAsync(other, OtherFilm));
        var connections = await scenario.Admin.GetAsync($"{Api}/media-managers/connections");
        Assert.Contains(connections.Elements, row => (int)row!["id"]! == connectionId);
        Assert.Equal(usersBefore - 1, await ProfileUsersAsync(scenario, profile));
        await AssertNothingLeftForAsync(scenario, (int)linked["id"]!);
    }

    /// <summary>How many workflows use the profile; the request fails if the profile is gone.</summary>
    private static async Task<int> ProfileUsersAsync(Scenario scenario, int profile)
    {
        var profiles = await scenario.Admin.GetAsync($"{Api}/processing/rule-sets");
        return (int)Assert.Single(profiles.Elements, row => (int)row!["id"]! == profile)!["used_by_library_count"]!;
    }

    /// <summary>With the server stopped: no table that keeps a workflow's rows still holds one, and no job waits to run for it.</summary>
    private static async Task AssertNothingLeftForAsync(Scenario scenario, int libraryId)
    {
        await using var database = await scenario.StopForDatabaseAsync();
        var tables = SeedSql.Rows(
            database.Connection,
            "SELECT DISTINCT m.name AS name FROM sqlite_master m, pragma_table_info(m.name) p WHERE m.type = 'table' AND p.name = 'library_id'");
        Assert.NotEmpty(tables);
        foreach (var table in tables.Select(row => (string)row["name"]!))
        {
            var left = SeedSql.Rows(database.Connection, $"SELECT count(*) AS n FROM \"{table}\" WHERE library_id = $id", ("$id", libraryId));
            Assert.True((long)left[0]["n"]! == 0, $"{table} still holds rows for the removed workflow");
        }

        var waiting = SeedSql.Rows(
            database.Connection,
            "SELECT count(*) AS n FROM jobs WHERE status IN ('pending', 'leased') AND payload_json LIKE $payload",
            ("$payload", $"%\"library_id\":{libraryId}%"));
        Assert.Equal(0L, (long)waiting[0]["n"]!);
    }
}
