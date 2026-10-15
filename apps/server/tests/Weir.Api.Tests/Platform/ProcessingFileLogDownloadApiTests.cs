using System.Net;
using Weir.Core.Time;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// "Download log" on a file's history: the whole record as a plain-text file a person can attach to a report, named after the file,
/// with every entry the log holds, newest first.
/// </summary>
public sealed class ProcessingFileLogDownloadApiTests
{
    private const string RelativePath = "Heat (1995)/Heat (1995) [1080p].mkv";

    private static string DownloadPath(long fileId) => $"/api/v1/processing/files/{fileId}/log/download";

    private static async Task<(WeirTestServer Server, ApiTestClient Client, long LibraryId)> StartAsync()
    {
        var server = await ApiTestClient.StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var libraryId = await TestDatabase.ScalarAsync(
            server, "INSERT INTO libraries (name, media_type) VALUES ('Films', 'movie') RETURNING id");
        return (server, client, libraryId);
    }

    private static Task<long> SeedFileAsync(WeirTestServer server, long libraryId, string relativePath) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, 'processed', CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", relativePath));

    private static Task SeedEntryAsync(WeirTestServer server, long fileId, long libraryId, string relativePath, DateTime recordedAt, string outcome, string title, string detailJson) =>
        TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO file_logs (file_id, library_id, relative_path, library_name, outcome, title, detail_json, recorded_at) " +
            "VALUES ($file, $lib, $path, 'Films', $outcome, $title, $detail, $at)",
            ("$file", fileId), ("$lib", libraryId), ("$path", relativePath), ("$outcome", outcome), ("$title", title),
            ("$detail", detailJson), ("$at", Timestamp.FromUtc(recordedAt).ToSqlite()));

    [Fact]
    public async Task The_whole_log_downloads_as_a_text_file_named_after_the_file_with_every_entry_newest_first()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var fileId = await SeedFileAsync(server, libraryId, RelativePath);
        var day = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        await SeedEntryAsync(server, fileId, libraryId, RelativePath, day, "failed", "Remux failed", """{"reason": "boom"}""");
        await SeedEntryAsync(server, fileId, libraryId, RelativePath, day.AddDays(1), "skipped", "Skipped: already done", """{"reason": "unchanged"}""");
        await SeedEntryAsync(server, fileId, libraryId, RelativePath, day.AddDays(2), "processed", "Cleaned", """{"removed_tracks": 2}""");

        using var response = await client.GetAsync(DownloadPath(fileId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", Header(response, "Content-Type"));
        Assert.Equal("attachment; filename=\"weir-Heat1995Heat19951080p.mkv.log.txt\"", Header(response, "Content-Disposition"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.StartsWith($"Weir — processing record for {RelativePath}\nWorkflow: Films\nRecords retained: 3 (newest first)\n", text, StringComparison.Ordinal);
        var cleaned = text.IndexOf("Cleaned", StringComparison.Ordinal);
        var skipped = text.IndexOf("Skipped: already done", StringComparison.Ordinal);
        var failed = text.IndexOf("Remux failed", StringComparison.Ordinal);
        Assert.True(cleaned > 0 && cleaned < skipped && skipped < failed, text);
        Assert.Contains("removed_tracks: 2", text, StringComparison.Ordinal);
        Assert.Contains("reason: unchanged", text, StringComparison.Ordinal);
        Assert.Contains("reason: boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_with_nothing_retained_downloads_a_note_saying_so()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var fileId = await SeedFileAsync(server, libraryId, "Up (2009)/up.mkv");

        using var response = await client.GetAsync(DownloadPath(fileId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Weir has no retained processing records for this file.\n", await response.Content.ReadAsStringAsync());
        Assert.Contains("filename=\"weir-Up2009up.mkv.log.txt\"", Header(response, "Content-Disposition"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Amélie (2001)/Amélie.mkv", "weir-Amélie2001Amélie.mkv.log.txt")]
    [InlineData("千と千尋の神隠し (2001)/千と千尋の神隠し.mkv", "weir-千と千尋の神隠し2001千と千尋の神隠し.mkv.log.txt")]
    public async Task A_file_with_letters_outside_ascii_in_its_name_downloads_under_its_own_name(string relativePath, string expectedName)
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var fileId = await SeedFileAsync(server, libraryId, relativePath);

        using var response = await client.GetAsync(DownloadPath(fileId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = Header(response, "Content-Disposition");
        Assert.All(disposition, character => Assert.InRange(character, ' ', '~'));
        const string Prefix = "attachment; filename*=utf-8''";
        Assert.StartsWith(Prefix, disposition, StringComparison.Ordinal);
        Assert.Equal(expectedName, Uri.UnescapeDataString(disposition[Prefix.Length..]));
    }

    [Fact]
    public async Task A_name_with_nothing_usable_in_it_downloads_as_file()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var fileId = await SeedFileAsync(server, libraryId, "((( )))/[]");

        using var response = await client.GetAsync(DownloadPath(fileId));

        Assert.Equal("attachment; filename=\"weir-file.log.txt\"", Header(response, "Content-Disposition"));
    }

    [Fact]
    public async Task Only_the_entries_of_that_file_are_in_its_download()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        var fileId = await SeedFileAsync(server, libraryId, RelativePath);
        var otherId = await SeedFileAsync(server, libraryId, "Up (2009)/up.mkv");
        var day = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        await SeedEntryAsync(server, fileId, libraryId, RelativePath, day, "processed", "Heat cleaned", "{}");
        await SeedEntryAsync(server, otherId, libraryId, "Up (2009)/up.mkv", day, "processed", "Up cleaned", "{}");

        using var response = await client.GetAsync(DownloadPath(fileId));

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Heat cleaned", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Up cleaned", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_Weir_has_no_record_of_is_refused_with_not_found()
    {
        var (server, client, _) = await StartAsync();
        await using var __ = server;

        using var response = await client.GetAsync(DownloadPath(99999));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Weir has no record of that file.", await Detail(response));
    }

    [Fact]
    public async Task The_download_needs_a_session_but_not_an_operator()
    {
        var (server, _, libraryId) = await StartAsync();
        await using var __ = server;
        var fileId = await SeedFileAsync(server, libraryId, RelativePath);
        await TestDatabase.SeedViewerAsync(server);
        var viewer = new ApiTestClient(server);
        await viewer.SignInAsync("bob", ViewerPassword);

        using var anonymous = await new ApiTestClient(server).GetAsync(DownloadPath(fileId));
        using var asViewer = await viewer.GetAsync(DownloadPath(fileId));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asViewer.StatusCode);
    }
}
