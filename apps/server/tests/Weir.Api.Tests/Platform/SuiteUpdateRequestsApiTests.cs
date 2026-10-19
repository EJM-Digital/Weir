using System.Net;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// The buttons in System › About ask the tray for a step by writing a flag file in the data folder: check now, download now,
/// or restart and apply. The state the page reads says the step is under way from the moment it is asked for, and the tray's
/// own answer, written to update-state.json, takes over from there. All of it needs a tray to answer: a Docker or source install
/// has none, and one that has gone quiet is refused with the reason.
/// </summary>
public sealed class SuiteUpdateRequestsApiTests
{
    private const string CheckUpdate = "/api/v1/suite/check-update";
    private const string DownloadUpdate = "/api/v1/suite/download-update";
    private const string UpdateState = "/api/v1/suite/update-state";

    private const string NoTray = "The Weir tray isn't running, so Weir can't update itself from here.";

    private const string NotInstalled =
        "This copy of Weir wasn't installed with the installer, so it can't update itself. To get a newer version, download the newest Weir-win-Setup.exe from the Weir releases page on GitHub and run it.";

    private static readonly string[] Steps = ["/api/v1/suite/check-update", "/api/v1/suite/download-update", "/api/v1/suite/apply-update"];

    /// <summary>
    /// A Windows install whose tray has just said it is alive, unless <paramref name="trayBeatAgo"/> says it did so longer ago. It has
    /// update checking switched on unless <paramref name="checking"/> says it has not, as a new install does not.
    /// </summary>
    private static async Task<(WeirTestServer Server, ApiTestClient Client)> SignedInAdminAsync(TimeSpan? trayBeatAgo = null, bool windows = true, bool checking = true)
    {
        var server = windows ? await StartServerAsync(("WEIR_RUNTIME", "windows")) : await StartServerAsync();
        await TestDatabase.SeedAdminAsync(server);
        if (checking)
        {
            File.WriteAllText(Path.Join(server.Home, "update-settings.json"), "{\"mode\": \"Auto\", \"checkForUpdates\": true}");
        }

        if (trayBeatAgo is { } ago)
        {
            File.WriteAllText(Path.Join(server.Home, "tray-heartbeat.json"), $"{{\"at\": \"{DateTimeOffset.UtcNow - ago:O}\"}}");
        }
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static Task<(WeirTestServer Server, ApiTestClient Client)> SignedInAdminWithTrayAsync() => SignedInAdminAsync(TimeSpan.Zero);

    private static void TrayWrites(WeirTestServer server, string json) =>
        File.WriteAllText(Path.Join(server.Home, "update-state.json"), json);

    private static async Task<HttpResponseMessage> AskAsync(ApiTestClient client, string path) =>
        await client.PostAsync(path, new { csrf_token = await client.CsrfAsync() });

    private static async Task<(string State, bool Downloaded, string? Version, string? Failure)> StateAsync(ApiTestClient client)
    {
        using var response = await client.GetAsync(UpdateState);
        var body = await Json(response);
        return (body["state"]!.GetValue<string>(), body["downloaded"]!.GetValue<bool>(), body["pending_version"]?.GetValue<string>(), body["failure"]?.GetValue<string>());
    }

    private static async Task<bool> TrayRunningAsync(ApiTestClient client)
    {
        using var response = await client.GetAsync(UpdateState);
        return (await Json(response))["tray_running"]!.GetValue<bool>();
    }

    [Theory]
    [InlineData(CheckUpdate)]
    [InlineData(DownloadUpdate)]
    public async Task While_update_checking_is_off_a_check_or_a_download_is_refused_and_leaves_no_flag(string path)
    {
        var (server, client) = await SignedInAdminAsync(TimeSpan.Zero, checking: false);
        await using var _ = server;

        using var refused = await AskAsync(client, path);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Update checking is off. Turn it on in System, About, then try again.", (await Json(refused))["detail"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
        Assert.Equal(("idle", false, null, null), await StateAsync(client));
    }

    private const string Settings = "/api/v1/suite/update-settings";

    private static async Task<HttpResponseMessage> SaveAsync(ApiTestClient client, object choices) =>
        await client.PutAsync(Settings, choices);

    [Fact]
    public async Task Turning_update_checking_on_or_off_tells_the_tray_so_it_looks_at_once()
    {
        var (server, client) = await SignedInAdminAsync(TimeSpan.Zero, checking: false);
        await using var _ = server;

        using var on = await SaveAsync(client, new { csrf_token = await client.CsrfAsync(), mode = "Auto", check_for_updates = true });
        Assert.True((await Json(on))["check_for_updates"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Join(server.Home, "update-check-now")));
        File.Delete(Path.Join(server.Home, "update-check-now"));

        using var sameAgain = await SaveAsync(client, new { csrf_token = await client.CsrfAsync(), mode = "NotifyOnly", check_for_updates = true });
        Assert.Equal(HttpStatusCode.OK, sameAgain.StatusCode);
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));

        using var off = await SaveAsync(client, new { csrf_token = await client.CsrfAsync(), mode = "NotifyOnly", check_for_updates = false });
        Assert.False((await Json(off))["check_for_updates"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Join(server.Home, "update-check-now")));
    }

    [Fact]
    public async Task Saving_the_update_settings_without_the_choice_leaves_it_as_it_was()
    {
        var (server, client) = await SignedInAdminAsync(TimeSpan.Zero, checking: false);
        await using var _ = server;
        using var changed = await SaveAsync(client, new { csrf_token = await client.CsrfAsync(), mode = "DownloadOnly", check_on_startup = false, check_interval_minutes = 240 });

        var settings = await Json(changed);

        Assert.False(settings["check_for_updates"]!.GetValue<bool>());
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
        Assert.Contains("\"checkForUpdates\": false", File.ReadAllText(Path.Join(server.Home, "update-settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_tray_to_tell_the_choice_is_saved_and_no_flag_is_left()
    {
        var (server, client) = await SignedInAdminAsync(checking: false);
        await using var _ = server;

        using var on = await SaveAsync(client, new { csrf_token = await client.CsrfAsync(), mode = "Auto", check_for_updates = true });

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
        Assert.True((await Json(await client.GetAsync(Settings)))["check_for_updates"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_update_already_downloaded_can_still_be_applied_while_update_checking_is_off()
    {
        var (server, client) = await SignedInAdminAsync(TimeSpan.Zero, checking: false);
        await using var _ = server;
        TrayWrites(server, """{"state": "downloaded", "downloaded": true, "version": "9.9.9"}""");

        using var applied = await AskAsync(client, "/api/v1/suite/apply-update");

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True(File.Exists(Path.Join(server.Home, "update-apply-now")));
    }

    [Fact]
    public async Task Nothing_under_way_reads_as_idle()
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;

        Assert.Equal(("idle", false, null, null), await StateAsync(client));
    }

    [Theory]
    [InlineData(CheckUpdate, "update-check-now", "checking")]
    [InlineData(DownloadUpdate, "update-download-now", "downloading")]
    public async Task Asking_writes_the_flag_for_the_tray_and_the_state_says_the_step_is_under_way(string path, string flag, string step)
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;

        using var asked = await AskAsync(client, path);

        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Equal(step, (await Json(asked))["state"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Join(server.Home, flag)));
        Assert.Equal((step, false, null, null), await StateAsync(client));
    }

    [Theory]
    [InlineData(CheckUpdate)]
    [InlineData(DownloadUpdate)]
    public async Task Asking_again_while_the_step_is_under_way_is_answered_without_a_second_flag(string path)
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;
        using var first = await AskAsync(client, path);
        var flag = Directory.GetFiles(server.Home, "update-*-now").Single();
        File.Delete(flag);
        TrayWrites(server, path == CheckUpdate ? """{"state": "checking", "downloaded": false}""" : """{"state": "downloading", "downloaded": false, "version": "9.9.9"}""");

        using var again = await AskAsync(client, path);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
    }

    [Fact]
    public async Task A_download_cannot_be_asked_for_while_a_check_runs_and_a_check_not_while_a_download_runs()
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;

        TrayWrites(server, """{"state": "checking", "downloaded": false}""");
        using var downloadDuringCheck = await AskAsync(client, DownloadUpdate);
        TrayWrites(server, """{"state": "downloading", "downloaded": false, "version": "9.9.9"}""");
        using var checkDuringDownload = await AskAsync(client, CheckUpdate);

        Assert.Equal(HttpStatusCode.Conflict, downloadDuringCheck.StatusCode);
        Assert.Equal("Weir is still checking for updates.", (await Json(downloadDuringCheck))["detail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, checkDuringDownload.StatusCode);
        Assert.Equal("Weir is downloading an update right now.", (await Json(checkDuringDownload))["detail"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
    }

    [Theory]
    [InlineData(CheckUpdate)]
    [InlineData(DownloadUpdate)]
    public async Task Nothing_is_asked_for_once_the_update_is_downloaded(string path)
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;
        TrayWrites(server, """{"state": "downloaded", "downloaded": true, "version": "9.9.9"}""");

        using var asked = await AskAsync(client, path);

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.Equal("An update is already downloaded. Restart Weir to apply it.", (await Json(asked))["detail"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
    }

    [Fact]
    public async Task A_failed_step_reads_with_its_reason_until_the_next_one_is_asked_for()
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;
        TrayWrites(server, """{"state": "failed", "downloaded": false, "version": "9.9.9", "failure": "Weir could not download the update."}""");

        Assert.Equal(("failed", false, "9.9.9", "Weir could not download the update."), await StateAsync(client));

        using var asked = await AskAsync(client, DownloadUpdate);

        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Equal(("downloading", false, "9.9.9", null), await StateAsync(client));
    }

    [Fact]
    public async Task A_state_the_tray_did_not_write_properly_reads_as_idle()
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;

        TrayWrites(server, """{"state": "exploding", "downloaded": false, "failure": "ignored"}""");
        Assert.Equal(("idle", false, null, null), await StateAsync(client));

        TrayWrites(server, """{"state": "checking", "downloaded": true, "version": "9.9.9"}""");
        Assert.Equal(("downloaded", true, "9.9.9", null), await StateAsync(client));
    }

    [Theory]
    [InlineData(CheckUpdate)]
    [InlineData(DownloadUpdate)]
    public async Task Only_a_signed_in_administrator_with_a_token_can_ask(string path)
    {
        var (server, admin) = await SignedInAdminWithTrayAsync();
        await using var _ = server;
        await TestDatabase.SeedUserAsync(server, "opal", "operator-password-here", "operator");
        var operatorClient = new ApiTestClient(server);
        await operatorClient.SignInAsync("opal", "operator-password-here");

        using var anonymous = await new ApiTestClient(server).PostAsync(path, new { csrf_token = "x" });
        using var notAdmin = await AskAsync(operatorClient, path);
        using var noToken = await admin.PostAsync(path, new { });
        using var wrongToken = await admin.PostAsync(path, new { csrf_token = "not-a-token" });
        using var extra = await admin.PostAsync(path, new { csrf_token = await admin.CsrfAsync(), step = "now" });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, extra.StatusCode);
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
    }

    [Fact]
    public async Task The_state_says_the_tray_is_running_while_it_is_beating()
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;

        Assert.True(await TrayRunningAsync(client));
    }

    [Theory]
    [InlineData(CheckUpdate)]
    [InlineData(DownloadUpdate)]
    [InlineData("/api/v1/suite/apply-update")]
    public async Task A_docker_or_source_install_has_no_tray_so_nothing_is_asked_for(string path)
    {
        var (server, client) = await SignedInAdminAsync(TimeSpan.Zero, windows: false);
        await using var _ = server;
        TrayWrites(server, """{"state": "downloaded", "downloaded": true, "version": "9.9.9"}""");

        using var asked = await AskAsync(client, path);

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.Equal(NoTray, (await Json(asked))["detail"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
        Assert.False(await TrayRunningAsync(client));
    }

    [Theory]
    [InlineData(CheckUpdate)]
    [InlineData(DownloadUpdate)]
    [InlineData("/api/v1/suite/apply-update")]
    public async Task A_tray_that_has_gone_quiet_is_not_asked(string path)
    {
        var (server, client) = await SignedInAdminAsync(TimeSpan.FromMinutes(10));
        await using var _ = server;
        TrayWrites(server, """{"state": "downloaded", "downloaded": true, "version": "9.9.9"}""");

        using var asked = await AskAsync(client, path);

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.Equal(NoTray, (await Json(asked))["detail"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
        Assert.False(await TrayRunningAsync(client));
    }

    [Theory]
    [InlineData(CheckUpdate)]
    [InlineData(DownloadUpdate)]
    [InlineData("/api/v1/suite/apply-update")]
    public async Task A_tray_run_from_a_copy_that_was_not_installed_says_so_and_is_not_asked(string path)
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;
        File.WriteAllText(Path.Join(server.Home, "tray-heartbeat.json"), $"{{\"at\": \"{DateTimeOffset.UtcNow:O}\", \"installed\": false}}");
        TrayWrites(server, """{"state": "downloaded", "downloaded": true, "version": "9.9.9"}""");

        using var asked = await AskAsync(client, path);
        using var stateResponse = await client.GetAsync(UpdateState);
        var state = await Json(stateResponse);

        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.Equal(NotInstalled, (await Json(asked))["detail"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
        Assert.False(state["tray_running"]!.GetValue<bool>());
        Assert.Equal(NotInstalled, state["cannot_update_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_missing_tray_gives_no_reason_beyond_not_running()
    {
        var (server, client) = await SignedInAdminAsync();
        await using var _ = server;

        using var stateResponse = await client.GetAsync(UpdateState);

        Assert.Null((await Json(stateResponse))["cannot_update_reason"]);
    }

    [Fact]
    public async Task Restart_and_apply_writes_its_flag_once_an_update_is_downloaded_and_is_refused_before()
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;

        using var before = await AskAsync(client, "/api/v1/suite/apply-update");
        TrayWrites(server, """{"state": "downloaded", "downloaded": true, "version": "9.9.9"}""");
        using var after = await AskAsync(client, "/api/v1/suite/apply-update");

        Assert.Equal(HttpStatusCode.Conflict, before.StatusCode);
        Assert.Equal("No downloaded update is pending.", (await Json(before))["detail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.True(File.Exists(Path.Join(server.Home, "update-apply-now")));
    }

    [Fact]
    public async Task A_tray_that_never_beat_is_not_asked()
    {
        var (server, client) = await SignedInAdminAsync(trayBeatAgo: null);
        await using var _ = server;

        foreach (var path in Steps)
        {
            using var asked = await AskAsync(client, path);
            Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        }
        Assert.Empty(Directory.GetFiles(server.Home, "update-*-now"));
    }

    [Theory]
    [InlineData("checking")]
    [InlineData("downloading")]
    public async Task A_step_a_dead_tray_said_it_was_at_reads_as_idle(string step)
    {
        var (server, client) = await SignedInAdminAsync(TimeSpan.FromMinutes(10));
        await using var _ = server;
        TrayWrites(server, $"{{\"state\": \"{step}\", \"downloaded\": false, \"version\": \"9.9.9\"}}");

        Assert.Equal(("idle", false, "9.9.9", null), await StateAsync(client));
    }

    [Fact]
    public async Task A_flag_nobody_took_for_minutes_does_not_read_as_a_step_under_way()
    {
        var (server, client) = await SignedInAdminWithTrayAsync();
        await using var _ = server;
        var flag = Path.Join(server.Home, "update-check-now");
        File.WriteAllText(flag, string.Empty);
        File.SetLastWriteTimeUtc(flag, DateTime.UtcNow - TimeSpan.FromMinutes(10));

        Assert.Equal(("idle", false, null, null), await StateAsync(client));
    }
}
