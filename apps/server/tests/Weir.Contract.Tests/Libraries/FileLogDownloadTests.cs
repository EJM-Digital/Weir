using System.Net;
using Microsoft.Data.Sqlite;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// "Download log" on a file's history, from a real running server: the whole record as a plain-text file named after the file,
/// with every entry the log holds, newest first, for any file name a library can hold.
/// </summary>
[ContractArea("libraries")]
public sealed class FileLogDownloadTests(FileLogDownloadTests.LogsFixture fixture)
    : IClassFixture<FileLogDownloadTests.LogsFixture>
{
    private const string Files = Api + "/processing/files";
    private const string Heat = "Heat (1995)/Heat (1995) [1080p].mkv";
    private const string Amelie = "Amélie (2001)/Amélie.mkv";
    private const string Spirited = "千と千尋の神隠し (2001)/千と千尋の神隠し.mkv";

    private static async Task<long> FileIdAsync(WeirClient client, string relativePath)
    {
        var response = await client.GetAsync(Files, ("path_contains", relativePath));
        response.ShouldBe(HttpStatusCode.OK);
        return (long)Assert.Single(
            response.Fields["files"]!.AsArray(),
            file => (string)file!["relative_path"]! == relativePath)!["id"]!;
    }

    [Fact]
    public async Task The_whole_log_downloads_as_a_text_file_named_after_the_file_with_every_entry_newest_first()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var fileId = await FileIdAsync(viewer, Heat);

        var response = await viewer.GetAsync($"{Files}/{fileId}/log/download");

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal("text/plain; charset=utf-8", response.Header("Content-Type"));
        Assert.Equal("attachment; filename=\"weir-Heat1995Heat19951080p.mkv.log.txt\"", response.Header("Content-Disposition"));
        Assert.StartsWith($"Weir — processing record for {Heat}\nWorkflow: Movies\nRecords retained: 3 (newest first)\n", response.Text, StringComparison.Ordinal);
        var cleaned = response.Text.IndexOf("Cleaned", StringComparison.Ordinal);
        var skipped = response.Text.IndexOf("Skipped: already done", StringComparison.Ordinal);
        var failed = response.Text.IndexOf("Remux failed", StringComparison.Ordinal);
        Assert.True(cleaned > 0 && cleaned < skipped && skipped < failed, response.Text);
        Assert.Contains("removed_tracks: 2", response.Text, StringComparison.Ordinal);
        Assert.Contains("reason: unchanged", response.Text, StringComparison.Ordinal);
        Assert.Contains("reason: boom", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_with_nothing_retained_downloads_a_note_saying_so()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var fileId = await FileIdAsync(viewer, "Up (2009)/up.mkv");

        var response = await viewer.GetAsync($"{Files}/{fileId}/log/download");

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Weir has no retained processing records for this file.\n", response.Text);
    }

    [Theory]
    [InlineData(Amelie, "weir-Amélie2001Amélie.mkv.log.txt")]
    [InlineData(Spirited, "weir-千と千尋の神隠し2001千と千尋の神隠し.mkv.log.txt")]
    public async Task A_file_with_letters_outside_ascii_in_its_name_downloads_under_its_own_name(string relativePath, string expectedName)
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var fileId = await FileIdAsync(viewer, relativePath);

        var response = await viewer.GetAsync($"{Files}/{fileId}/log/download");

        response.ShouldBe(HttpStatusCode.OK);
        Assert.StartsWith($"Weir — processing record for {relativePath}\n", response.Text, StringComparison.Ordinal);
        var disposition = response.Header("Content-Disposition")!;
        Assert.All(disposition, character => Assert.InRange(character, ' ', '~'));
        const string Prefix = "attachment; filename*=utf-8''";
        Assert.StartsWith(Prefix, disposition, StringComparison.Ordinal);
        Assert.Equal(expectedName, Uri.UnescapeDataString(disposition[Prefix.Length..]));
    }

    [Fact]
    public async Task The_download_of_a_file_Weir_has_no_record_of_is_refused_with_not_found()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);

        (await viewer.GetAsync($"{Files}/999999/log/download")).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_download_needs_a_session()
    {
        using var client = fixture.Server.CreateClient();

        (await client.GetAsync($"{Files}/1/log/download")).ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>A viewer, and files with their logs (seeded while the server is stopped).</summary>
    public sealed class LogsFixture : SeededServerFixture
    {
        protected override void Seed(SqliteConnection connection)
        {
            SeededAccounts.EnsureViewer(connection);
            var movies = LibraryIdNamed(connection, "Movies");
            var heat = InsertFile(connection, movies, Heat, ("status", "processed"), ("last_seen_at", Now()));
            InsertFile(connection, movies, "Up (2009)/up.mkv", ("status", "processed"), ("last_seen_at", Now()));
            var amelie = InsertFile(connection, movies, Amelie, ("status", "processed"), ("last_seen_at", Now()));
            var spirited = InsertFile(connection, movies, Spirited, ("status", "processed"), ("last_seen_at", Now()));
            var day = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            AddEntry(connection, heat, movies, Heat, day, "failed", "Remux failed", Obj(("reason", "boom")).ToJsonString());
            AddEntry(connection, heat, movies, Heat, day.AddDays(1), "skipped", "Skipped: already done", Obj(("reason", "unchanged")).ToJsonString());
            AddEntry(connection, heat, movies, Heat, day.AddDays(2), "processed", "Cleaned", Obj(("removed_tracks", 2)).ToJsonString());
            AddEntry(connection, amelie, movies, Amelie, day, "processed", "Cleaned", "{}");
            AddEntry(connection, spirited, movies, Spirited, day, "processed", "Cleaned", "{}");
        }

        private static void AddEntry(SqliteConnection connection, long fileId, long libraryId, string path, DateTime recordedAt, string outcome, string title, string detailJson) =>
            SeedSql.Execute(
                connection,
                "INSERT INTO file_logs (file_id, library_id, relative_path, library_name, outcome, title, detail_json, recorded_at) "
                    + "VALUES ($file, $library, $path, 'Movies', $outcome, $title, $detail, $at)",
                ("$file", fileId), ("$library", libraryId), ("$path", path), ("$outcome", outcome), ("$title", title),
                ("$detail", detailJson), ("$at", SeedSql.UtcText(recordedAt)));
    }
}
