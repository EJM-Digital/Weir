using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The file routes at their edges: removing a finished file when a delete was asked for, queueing more files at once than Weir
/// takes, and asking for a file to be passed through when its workflow is gone or has no watched folder.
/// </summary>
public sealed class ProcessingFilesEdgesApiTests
{
    private const string Enqueue = "/api/v1/processing/jobs/file-remux-pass/enqueue";

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    [Fact]
    public async Task Removing_a_finished_file_when_a_delete_was_asked_for_only_takes_it_off_the_list()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var watched = Directory.CreateDirectory(Path.Join(server.Home, "watched")).FullName;
        var path = Path.Join(watched, "film.mkv");
        await File.WriteAllBytesAsync(path, "done"u8.ToArray());
        var libraryId = await TestDatabase.ScalarAsync(
            server, "INSERT INTO libraries (name, media_type, watched_folder, display_order) VALUES ('Films', 'movie', $w, 1) RETURNING id", ("$w", watched));
        var fileId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($l, 'film.mkv', 'processed', CURRENT_TIMESTAMP) RETURNING id",
            ("$l", libraryId));

        using var response = await client.SendAsync(
            HttpMethod.Delete, $"/api/v1/processing/files/{fileId}", new { csrf_token = await client.CsrfAsync(), resolution = "delete" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(File.Exists(path));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM files"));
    }

    [Fact]
    public async Task Queueing_more_than_a_thousand_files_at_once_is_refused_and_queues_nothing()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var queuedBefore = await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");

        using var response = await client.PostAsync(
            "/api/v1/processing/files/requeue", new { csrf_token = await client.CsrfAsync(), file_ids = Enumerable.Range(1, 1001).ToArray() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("List should have at most 1000 items after validation, not 1001", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(queuedBefore, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }

    [Fact]
    public async Task Passing_a_file_through_for_a_workflow_that_is_gone_is_a_404_in_plain_words()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;

        using var response = await client.PostAsync(Enqueue, new { csrf_token = await client.CsrfAsync(), relative_media_path = "film.mkv", library_id = 999 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("The selected workflow no longer exists. Refresh Workflows and try again.", await Detail(response));
    }

    [Theory]
    [InlineData("movie", "Movies")]
    [InlineData("tv", "TV")]
    public async Task Passing_a_file_through_for_a_workflow_with_no_watched_folder_says_what_to_set(string scope, string label)
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        await TestDatabase.ExecuteAsync(server, "UPDATE libraries SET watched_folder = '' WHERE media_type = $t", ("$t", scope));

        using var response = await client.PostAsync(Enqueue, new { csrf_token = await client.CsrfAsync(), relative_media_path = "film.mkv", media_scope = scope });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal($"{label} has no watched folder yet, so Weir cannot find that file. Set the watched folder on the {label} workflow, then try again.", await Detail(response));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }
}
