using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Core.Updates;
using Weir.Infrastructure.Http;
using Weir.Tests.Shared;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>How Weir tells the owner about updates: what the saved choice is, and what the newest release is when GitHub answers as it did when recorded.</summary>
public sealed class UpdateApiTests
{
    private static async Task<(WeirTestServer Server, ApiTestClient Admin)> SignedInAdminAsync(
        string version, RecordedGitHub? github = null)
    {
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_VERSION", version)],
            configureServices: github is null
                ? null
                : services => services.AddSingleton<IReleaseCatalogClient>(provider => new GitHubReleaseCatalogClient(
                    provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<GitHubReleaseCatalogClient>>(), github)));
        await TestDatabase.SeedAdminAsync(server);
        var admin = new ApiTestClient(server);
        await admin.SignInAsync();
        return (server, admin);
    }

    [Fact]
    public async Task Update_status_says_an_update_is_available_to_a_copy_behind_the_recorded_release_feed()
    {
        using var github = new RecordedGitHub();
        var (server, admin) = await SignedInAdminAsync("1.0.0-rc.17", github);
        await using var _ = server;

        var body = await Json(await admin.GetAsync("/api/v1/suite/update-status"));

        Assert.Equal("update_available", body["status"]!.GetValue<string>());
        Assert.Equal("1.0.0-rc.17", body["current_version"]!.GetValue<string>());
        Assert.Equal("1.0.0-rc.18", body["latest_version"]!.GetValue<string>());
        Assert.Equal([ReleaseCatalog.ReleasesFeedUrl], github.Asked);
    }

    [Fact]
    public async Task Update_status_says_up_to_date_to_a_copy_that_is_the_newest_the_recorded_feed_names()
    {
        using var github = new RecordedGitHub();
        var (server, admin) = await SignedInAdminAsync("1.0.0-rc.18", github);
        await using var _ = server;

        var body = await Json(await admin.GetAsync("/api/v1/suite/update-status"));

        Assert.Equal("up_to_date", body["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_status_reads_the_recorded_releases_API_when_the_feed_cannot_be_read()
    {
        using var github = new RecordedGitHub { FeedIsDown = true };
        var (server, admin) = await SignedInAdminAsync("1.0.0-rc.17", github);
        await using var _ = server;

        var body = await Json(await admin.GetAsync("/api/v1/suite/update-status"));

        Assert.Equal("update_available", body["status"]!.GetValue<string>());
        Assert.Equal("1.0.0-rc.18", body["latest_version"]!.GetValue<string>());
        Assert.Equal([ReleaseCatalog.ReleasesFeedUrl, ReleaseCatalog.ReleasesUrl], github.Asked);
    }

    [Fact]
    public async Task Update_settings_are_read_only_by_someone_signed_in()
    {
        await using var server = await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);

        using var anonymous = await new ApiTestClient(server).GetAsync("/api/v1/suite/update-settings");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Update_settings_start_as_Auto_and_a_saved_choice_is_what_the_next_read_returns()
    {
        var (server, admin) = await SignedInAdminAsync("1.0.0");
        await using var _ = server;
        var first = await Json(await admin.GetAsync("/api/v1/suite/update-settings"));
        Assert.Equal(("Auto", true, 60), (first["mode"]!.GetValue<string>(), first["check_on_startup"]!.GetValue<bool>(), first["check_interval_minutes"]!.GetValue<int>()));

        using var saved = await admin.PutAsync(
            "/api/v1/suite/update-settings",
            new { csrf_token = await admin.CsrfAsync(), mode = "NotifyOnly", check_on_startup = false, check_interval_minutes = 240 });
        var again = await Json(await admin.GetAsync("/api/v1/suite/update-settings"));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(("NotifyOnly", false, 240), (again["mode"]!.GetValue<string>(), again["check_on_startup"]!.GetValue<bool>(), again["check_interval_minutes"]!.GetValue<int>()));
        Assert.Equal(again.ToJsonString(), (await Json(saved)).ToJsonString());
    }

    [Theory]
    [InlineData("automatic", 60)]
    [InlineData("auto", 60)]
    [InlineData("Auto", 0)]
    [InlineData("Auto", 10081)]
    public async Task Update_settings_refuse_a_mode_that_is_not_one_of_the_three_and_an_interval_outside_a_minute_to_a_week(string mode, int minutes)
    {
        var (server, admin) = await SignedInAdminAsync("1.0.0");
        await using var _ = server;
        await admin.PutAsync("/api/v1/suite/update-settings", new { csrf_token = await admin.CsrfAsync(), mode = "DownloadOnly", check_on_startup = true, check_interval_minutes = 120 });

        using var refused = await admin.PutAsync(
            "/api/v1/suite/update-settings",
            new { csrf_token = await admin.CsrfAsync(), mode, check_on_startup = true, check_interval_minutes = minutes });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var kept = await Json(await admin.GetAsync("/api/v1/suite/update-settings"));
        Assert.Equal(("DownloadOnly", 120), (kept["mode"]!.GetValue<string>(), kept["check_interval_minutes"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Update_settings_refuse_a_field_they_do_not_have()
    {
        var (server, admin) = await SignedInAdminAsync("1.0.0");
        await using var _ = server;

        using var refused = await admin.PutAsync(
            "/api/v1/suite/update-settings",
            new { csrf_token = await admin.CsrfAsync(), mode = "Auto", check_on_startup = true, install_at_night = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }
}
