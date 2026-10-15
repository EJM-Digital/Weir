using System.Net;
using Weir.Core.Time;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// A file's log belongs to its workflow and path: two workflows holding the same path never show each other's entries, in the
/// log or in its download. An entry written before Weir knew its workflow shows only where the path is held by one workflow.
/// </summary>
public sealed class ProcessingFileLogWorkflowApiTests
{
    private const string SharedPath = "Heat (1995)/heat.mkv";
    private const string SoloPath = "Solo (2001)/solo.mkv";

    private sealed record World(WeirTestServer Server, ApiTestClient Client, long FilmsFile, long KidsFile, long SoloFile);

    private static async Task<World> StartAsync()
    {
        var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var films = await SeedLibraryAsync(server, "Films");
        var kids = await SeedLibraryAsync(server, "Kids");
        var filmsFile = await SeedFileAsync(server, films, SharedPath);
        var kidsFile = await SeedFileAsync(server, kids, SharedPath);
        var soloFile = await SeedFileAsync(server, films, SoloPath);
        var day = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        await SeedEntryAsync(server, filmsFile, films, "Films", SharedPath, day, "Films pass one");
        await SeedEntryAsync(server, filmsFile, films, "Films", SharedPath, day.AddDays(1), "Films pass two");
        await SeedEntryAsync(server, kidsFile, kids, "Kids", SharedPath, day.AddHours(1), "Kids pass");
        await SeedEntryAsync(server, null, null, "", SharedPath, day.AddHours(2), "Old pass of a shared path");
        await SeedEntryAsync(server, null, null, "", SoloPath, day, "Old pass of a path held once");
        return new World(server, client, filmsFile, kidsFile, soloFile);
    }

    private static Task<long> SeedLibraryAsync(WeirTestServer server, string name) =>
        TestDatabase.ScalarAsync(server, "INSERT INTO libraries (name, media_type) VALUES ($name, 'movie') RETURNING id", ("$name", name));

    private static Task<long> SeedFileAsync(WeirTestServer server, long libraryId, string path) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, 'processed', CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", path));

    private static Task SeedEntryAsync(WeirTestServer server, long? fileId, long? libraryId, string libraryName, string path, DateTime recordedAt, string title) =>
        TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO file_logs (file_id, library_id, relative_path, library_name, outcome, title, detail_json, recorded_at) " +
            "VALUES ($file, $lib, $path, $name, 'processed', $title, '{}', $at)",
            ("$file", (object?)fileId ?? DBNull.Value), ("$lib", (object?)libraryId ?? DBNull.Value), ("$path", path),
            ("$name", libraryName), ("$title", title), ("$at", Timestamp.FromUtc(recordedAt).ToSqlite()));

    private static async Task<List<string>> ViewTitlesAsync(World world, long fileId)
    {
        using var response = await world.Client.GetAsync($"/api/v1/processing/files/{fileId}/log");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await Json(response))["entries"]!.AsArray().Select(entry => entry!["title"]!.GetValue<string>())];
    }

    private static async Task<string> DownloadAsync(World world, long fileId)
    {
        using var response = await world.Client.GetAsync($"/api/v1/processing/files/{fileId}/log/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Each_workflow_sees_only_its_own_entries_for_a_shared_path()
    {
        var world = await StartAsync();
        await using var _ = world.Server;

        Assert.Equal(["Films pass two", "Films pass one"], await ViewTitlesAsync(world, world.FilmsFile));
        Assert.Equal(["Kids pass"], await ViewTitlesAsync(world, world.KidsFile));
    }

    [Fact]
    public async Task Each_workflows_download_holds_only_its_own_entries_and_counts_only_those()
    {
        var world = await StartAsync();
        await using var _ = world.Server;

        var films = await DownloadAsync(world, world.FilmsFile);
        var kids = await DownloadAsync(world, world.KidsFile);

        Assert.Contains("Workflow: Films\nRecords retained: 2 (newest first)\n", films, StringComparison.Ordinal);
        Assert.Contains("Films pass one", films, StringComparison.Ordinal);
        Assert.DoesNotContain("Kids pass", films, StringComparison.Ordinal);
        Assert.Contains("Workflow: Kids\nRecords retained: 1 (newest first)\n", kids, StringComparison.Ordinal);
        Assert.Contains("Kids pass", kids, StringComparison.Ordinal);
        Assert.DoesNotContain("Films pass", kids, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_from_before_its_workflow_was_known_is_shown_only_where_one_workflow_holds_the_path()
    {
        var world = await StartAsync();
        await using var _ = world.Server;

        Assert.Equal(["Old pass of a path held once"], await ViewTitlesAsync(world, world.SoloFile));
        Assert.DoesNotContain("Old pass of a shared path", await ViewTitlesAsync(world, world.FilmsFile));
        Assert.DoesNotContain("Old pass of a shared path", await ViewTitlesAsync(world, world.KidsFile));
        Assert.DoesNotContain("Old pass of a shared path", await DownloadAsync(world, world.FilmsFile), StringComparison.Ordinal);
        Assert.Contains("Old pass of a path held once", await DownloadAsync(world, world.SoloFile), StringComparison.Ordinal);
    }
}
