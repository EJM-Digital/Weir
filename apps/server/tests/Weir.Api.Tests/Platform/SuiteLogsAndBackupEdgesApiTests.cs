using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weir.Infrastructure.Logging;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>The log view's filters on whether an entry carries an error and on how many to show, and "Back up now" when the backup folder cannot be written.</summary>
public sealed class SuiteLogsAndBackupEdgesApiTests
{
    private const string Backups = "/api/v1/suite/configuration-backups";

    private static async Task<(WeirTestServer Server, ApiTestClient Admin)> StartAsync()
    {
        var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static string Line(string message, string? traceback = null) =>
        "{\"timestamp\":\"2026-05-09T10:00:00Z\",\"level\":\"ERROR\",\"logger\":\"weir.test.edges\",\"message\":\"" + message + "\"" +
        (traceback is null ? string.Empty : ",\"traceback\":\"" + traceback + "\"") + "}";

    private static async Task<IReadOnlyList<string>> MessagesAsync(ApiTestClient client, string query)
    {
        using var response = await client.GetAsync("/api/v1/suite/logs?search=weirlogsedge&" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await Json(response))["items"]!.AsArray().Select(item => item!["message"]!.GetValue<string>())];
    }

    [Fact]
    public async Task The_log_view_can_show_only_entries_with_an_error_attached_or_only_those_without_and_no_more_than_a_limit()
    {
        var (server, client) = await StartAsync();
        await using var _ = server;
        var file = server.Services.GetRequiredService<WeirLogFile>();
        file.WriteLine(Line("weirlogsedge plain"));
        file.WriteLine(Line("weirlogsedge failed", "System.InvalidOperationException: nope"));
        file.WriteLine(Line("weirlogsedge also plain"));

        Assert.Equal(["weirlogsedge failed"], await MessagesAsync(client, "has_exception=true"));
        Assert.Equal(["weirlogsedge also plain", "weirlogsedge plain"], await MessagesAsync(client, "has_exception=false"));
        Assert.Single(await MessagesAsync(client, "limit=1"));
    }

    [Fact]
    public async Task Backing_up_now_when_the_backup_folder_cannot_be_written_says_what_to_check_and_lists_nothing()
    {
        var (server, admin) = await StartAsync();
        await using var _ = server;
        var folder = Path.Join(server.Home, "backups");
        Directory.Delete(folder, recursive: true);
        await File.WriteAllTextAsync(folder, "a file where the folder should be");

        using var response = await admin.PostAsync(Backups, new { csrf_token = await admin.CsrfAsync() });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Weir could not write the backup file. Check that Weir can write to its backup folder, then try again.", await Detail(response));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM suite_configuration_backup"));
    }

    [Theory]
    [InlineData("arr_library_operator_settings")]
    [InlineData("operator_settings")]
    public async Task Exporting_the_settings_when_a_saved_part_of_them_is_missing_says_so_in_plain_words(string missingTable)
    {
        var (server, admin) = await StartAsync();
        await using var _ = server;
        await TestDatabase.ExecuteAsync(server, $"DELETE FROM {missingTable}");

        using var response = await admin.GetAsync("/api/v1/suite/configuration-bundle");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Weir could not export its settings because part of what it has saved is missing. Check the server log, then try again.", await Detail(response));
    }
}
