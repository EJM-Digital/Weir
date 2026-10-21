using System.Net;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;
using static Weir.Api.Tests.Processing.LibraryViewApiTests;

namespace Weir.Api.Tests.Processing;

/// <summary>"Scan library": who may ask, what a library with no folders says, and that a second ask while a scan is waiting joins it.</summary>
public sealed class LibraryScanRequestApiTests
{
    private static string ScanPath(long libraryId) => $"/api/v1/processing/libraries/{libraryId}/library-scan";

    private static async Task SaveFoldersAsync(ApiTestClient client, long libraryId) =>
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsync(
                $"/api/v1/processing/libraries/{libraryId}/library-settings",
                new { library_folders = new[] { "/library/films" }, csrf_token = await client.CsrfAsync() })).StatusCode);

    [Fact]
    public async Task Only_an_operator_or_an_administrator_may_ask_for_a_scan()
    {
        var (server, _, libraryId) = await StartAsync();
        await using var _ = server;

        await Callers.AssertWhoMayCallAsync(server, HttpMethod.Post, ScanPath(libraryId), Callers.Operator);
    }

    [Fact]
    public async Task A_library_with_no_folders_is_not_scanned_and_says_to_add_one()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;

        using var response = await client.PostAsync(ScanPath(libraryId), new { csrf_token = await client.CsrfAsync() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Add at least one library folder before scanning.", await Detail(response));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.library.scan.v1'"));
    }

    [Fact]
    public async Task Asking_again_while_a_scan_is_waiting_joins_it_instead_of_queueing_another()
    {
        var (server, client, libraryId) = await StartAsync();
        await using var _ = server;
        await SaveFoldersAsync(client, libraryId);

        var first = await Json(await client.PostAsync(ScanPath(libraryId), new { csrf_token = await client.CsrfAsync() }));
        var second = await Json(await client.PostAsync(ScanPath(libraryId), new { csrf_token = await client.CsrfAsync() }));

        Assert.False(first["already_running"]!.GetValue<bool>());
        Assert.True(second["already_running"]!.GetValue<bool>());
        Assert.Equal(first["job_id"]!.GetValue<long>(), second["job_id"]!.GetValue<long>());
    }
}
