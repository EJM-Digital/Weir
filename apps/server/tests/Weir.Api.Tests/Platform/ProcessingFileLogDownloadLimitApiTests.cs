using System.Net;
using Weir.Core.Time;

namespace Weir.Api.Tests.Platform;

/// <summary>"Download log" on a file with more entries than one download holds: it says how many it shows and how many there are.</summary>
public sealed class ProcessingFileLogDownloadLimitApiTests
{
    private const string RelativePath = "Heat (1995)/heat.mkv";
    private const int Retained = 1234;

    private static async Task<(WeirTestServer Server, ApiTestClient Client, long FileId, long LibraryId)> StartWithLogAsync(int entries)
    {
        var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await TestDatabase.ScalarAsync(server, "INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        var fileId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, 'processed', CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", RelativePath));
        await TestDatabase.ExecuteAsync(
            server,
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < $count) " +
            "INSERT INTO file_logs (file_id, library_id, relative_path, library_name, outcome, title, detail_json, recorded_at) " +
            "SELECT $file, $lib, $path, 'Films', 'processed', 'Pass ' || i, '{}', datetime($start, '+' || i || ' minutes') FROM n",
            ("$count", entries), ("$file", fileId), ("$lib", libraryId), ("$path", RelativePath),
            ("$start", Timestamp.FromUtc(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)).ToSqlite()));
        return (server, client, fileId, libraryId);
    }

    [Fact]
    public async Task A_log_longer_than_one_download_holds_says_it_shows_the_newest_entries_and_how_many_there_are()
    {
        var (server, client, fileId, _) = await StartWithLogAsync(Retained);
        await using var _ = server;

        using var response = await client.GetAsync($"/api/v1/processing/files/{fileId}/log/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\nShowing the newest 500 of 1,234 entries\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Records retained", text, StringComparison.Ordinal);
        Assert.Contains($"Pass {Retained}\n", text, StringComparison.Ordinal);
        Assert.Contains($"Pass {Retained - 499}\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"Pass {Retained - 500}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_log_of_exactly_one_downloads_worth_is_not_called_cut_short()
    {
        var (server, client, fileId, _) = await StartWithLogAsync(500);
        await using var _ = server;

        using var response = await client.GetAsync($"/api/v1/processing/files/{fileId}/log/download");

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\nRecords retained: 500 (newest first)\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing the newest", text, StringComparison.Ordinal);
    }
}
