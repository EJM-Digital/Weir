using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Weir.Infrastructure.Logging;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>"Back up now" and the server log download, over real HTTP.</summary>
public sealed class SuiteFileApiTests
{
    private static async Task<(WeirTestServer Server, ApiTestClient Admin)> SignedInAdminAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static async Task<ApiTestClient> SignedInAsync(WeirTestServer server, string username, string role)
    {
        await TestDatabase.SeedUserAsync(server, username, "a-long-enough-password", role);
        var client = new ApiTestClient(server);
        await client.SignInAsync(username, "a-long-enough-password");
        return client;
    }

    [Fact]
    public async Task Backing_up_now_writes_a_snapshot_that_is_then_listed()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;

        using var created = await admin.PostAsync("/api/v1/suite/configuration-backups", new { csrf_token = await admin.CsrfAsync() });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await Json(created);
        var id = body["id"]!.GetValue<long>();
        Assert.Equal("manual", body["made_by"]!.GetValue<string>());
        var listed = (await Json(await admin.GetAsync("/api/v1/suite/configuration-backups")))["items"]!.AsArray();
        Assert.Equal(id, listed.Single()!["id"]!.GetValue<long>());
        Assert.Equal("manual", listed.Single()!["made_by"]!.GetValue<string>());
    }

    [Fact]
    public async Task Backing_up_now_is_refused_to_an_operator()
    {
        var (server, _) = await SignedInAdminAsync();
        await using var __ = server;
        var operatorClient = await SignedInAsync(server, "opal", "operator");

        using var refused = await operatorClient.PostAsync("/api/v1/suite/configuration-backups", new { csrf_token = await operatorClient.CsrfAsync() });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Backing_up_now_is_refused_to_an_anonymous_caller()
    {
        var server = await StartServerAsync();
        await using var _ = server;
        await TestDatabase.SeedAdminAsync(server);
        var anonymous = new ApiTestClient(server);

        using var refused = await anonymous.PostAsync("/api/v1/suite/configuration-backups", new { csrf_token = await anonymous.CsrfAsync() });

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Backing_up_now_rejects_a_missing_csrf_token()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;

        using var refused = await admin.PostAsync("/api/v1/suite/configuration-backups", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM suite_configuration_backup"));
    }

    [Fact]
    public async Task Backing_up_now_rejects_an_invalid_csrf_token()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;

        using var refused = await admin.PostAsync("/api/v1/suite/configuration-backups", new { csrf_token = "not-a-real-token" });

        Assert.Equal((HttpStatusCode.BadRequest, "Your confirmation token expired. Refresh the page and try again."), (refused.StatusCode, await Detail(refused)));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM suite_configuration_backup"));
    }

    [Fact]
    public async Task The_server_log_downloads_as_a_file_holding_every_line()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        var logFile = server.Services.GetRequiredService<WeirLogFile>();
        logFile.WriteLine("{\"timestamp\":\"2026-05-09T10:00:00Z\",\"level\":\"INFO\",\"logger\":\"weir.tests\",\"message\":\"first line\"}");
        logFile.WriteLine("{\"timestamp\":\"2026-05-09T10:00:01Z\",\"level\":\"ERROR\",\"logger\":\"weir.tests\",\"message\":\"second line\"}");

        using var download = await admin.GetAsync("/api/v1/suite/logs/download");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Matches("^attachment; filename=\"weir-log-\\d{8}-\\d{6}\\.log\"$", Header(download, "Content-Disposition"));
        var text = await download.Content.ReadAsStringAsync();
        Assert.Contains("first line", text, StringComparison.Ordinal);
        Assert.Contains("second line", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_viewer_cannot_download_the_server_log()
    {
        var (server, _) = await SignedInAdminAsync();
        await using var __ = server;
        var viewer = await SignedInAsync(server, "vera", "viewer");

        using var refused = await viewer.GetAsync("/api/v1/suite/logs/download");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task An_operator_cannot_download_the_server_log()
    {
        var (server, _) = await SignedInAdminAsync();
        await using var __ = server;
        var operatorClient = await SignedInAsync(server, "opal", "operator");

        using var refused = await operatorClient.GetAsync("/api/v1/suite/logs/download");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Downloading_the_log_is_refused_to_an_anonymous_caller()
    {
        var server = await StartServerAsync();
        await using var _ = server;
        var anonymous = new ApiTestClient(server);

        using var refused = await anonymous.GetAsync("/api/v1/suite/logs/download");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task The_log_download_is_never_cached()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;

        using var download = await admin.GetAsync("/api/v1/suite/logs/download");

        Assert.Equal("no-store, private", Header(download, "Cache-Control"));
    }

    [Fact]
    public async Task A_large_log_streams_in_full_while_logging_keeps_up()
    {
        var (server, admin) = await SignedInAdminAsync();
        await using var _ = server;
        var logFile = server.Services.GetRequiredService<WeirLogFile>();
        // Large enough that the old build-it-all-in-memory approach would be slow to walk line by line;
        // small enough the test still runs quickly.
        const int lineCount = 20_000;
        for (var i = 0; i < lineCount; i++)
        {
            logFile.WriteLine($"{{\"timestamp\":\"2026-05-09T10:00:00Z\",\"level\":\"INFO\",\"logger\":\"weir.tests\",\"message\":\"line {i}\"}}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/suite/logs/download");
        request.Headers.Add("Cookie", string.Join("; ", admin.Cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        using var download = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        await using var body = await download.Content.ReadAsStreamAsync();
        var firstChunk = new byte[4096];
        var firstRead = await body.ReadAsync(firstChunk);
        Assert.True(firstRead > 0);

        // The response is still being read (there is far more than one 4KB chunk to go): a write must not
        // wait for that to finish, because streaming the snapshot never touches the log's write lock.
        // A write that waited for the download would wait for ever, as nothing reads it until this write is done.
        await Task.Run(() => logFile.WriteLine("{\"timestamp\":\"2026-05-09T10:00:00Z\",\"level\":\"INFO\",\"logger\":\"weir.tests\",\"message\":\"written while streaming\"}"))
            .WaitAsync(TimeSpan.FromSeconds(15));

        await using var rest = new MemoryStream();
        await body.CopyToAsync(rest);
        var full = Encoding.UTF8.GetString(firstChunk, 0, firstRead) + Encoding.UTF8.GetString(rest.ToArray());
        Assert.Contains("\"line 0\"", full, StringComparison.Ordinal);
        Assert.Contains($"\"line {lineCount - 1}\"", full, StringComparison.Ordinal);
        // Counted by marker rather than total line count: sign-in and startup add a few lines of their own
        // ahead of the ones this test wrote, and only completeness of this test's own lines is the point.
        var matches = Regex.Count(full, "\"line \\d+\"");
        Assert.Equal(lineCount, matches);
        Assert.DoesNotContain("written while streaming", full, StringComparison.Ordinal);
    }
}
