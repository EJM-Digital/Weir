using System.Numerics;
using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Core.Updates;

namespace Weir.Core.Tests.Updates;

/// <summary>Reading a release from GitHub's JSON, the update status Weir shows for it, and the settings and state files shared with the tray.</summary>
public sealed class ReleasePayloadTests
{
    private static readonly GitHubReleaseAsset Setup = new("Weir-win-Setup.exe", "https://api.example/assets/1", "https://example/Weir-win-Setup.exe", 10, "application/octet-stream");

    private static GitHubReleaseRecord Release(string version = "1.2.0", params GitHubReleaseAsset[] assets) =>
        new("v" + version, version, "Weir " + version, "https://example/releases/v" + version, Timestamp.FromUtc(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)), false, false, assets);

    private static string Text(WireObject value, string key) => Assert.IsType<WireString>(value[key]).Value;

    private static WireObject Payload(string tag = "v1.2.0") => new WireObject().Set("tag_name", tag);

    [Fact]
    public void A_failed_request_carries_what_gitHub_said()
    {
        var limit = new ReleaseRateLimit(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), null);
        var inner = new InvalidOperationException("inner");

        Assert.Equal("slow", new ReleaseFetchException("slow").Message);
        Assert.Same(inner, new ReleaseFetchException("slow", inner).InnerException);
        Assert.NotNull(new ReleaseFetchException().Message);
        Assert.Equal(503, new ReleaseFetchException(503).StatusCode);
        Assert.Equal("GitHub answered HTTP 503.", new ReleaseFetchException(503).Message);
        Assert.Null(new ReleaseFetchException(503).RateLimit);
        Assert.Same(limit, new ReleaseFetchException(403, limit).RateLimit);
    }

    [Fact]
    public void An_asset_is_found_by_name_ignoring_case_and_spaces_and_the_installer_falls_back_to_its_old_name()
    {
        var legacy = new GitHubReleaseAsset("WeirSetup.exe", null, "https://example/WeirSetup.exe", 5, null);

        Assert.Same(Setup, Release("1.2.0", Setup).AssetNamed("  WEIR-WIN-SETUP.EXE "));
        Assert.Null(Release("1.2.0", Setup).AssetNamed("other.zip"));
        Assert.Same(legacy, Release("1.2.0", legacy).WindowsInstallerAsset());
        Assert.Same(Setup, Release("1.2.0", legacy, Setup).WindowsInstallerAsset());
        Assert.Null(Release().WindowsInstallerAsset());
        Assert.Throws<ArgumentNullException>(() => Release().AssetNamed(null!));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("v", null)]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData(" 1.0.0-rc.1 ", "1.0.0-rc.1")]
    public void A_release_tag_loses_its_v_and_a_tag_with_nothing_else_is_no_version(string? raw, string? version) =>
        Assert.Equal(version, ReleaseCatalog.NormalizeReleaseVersion(raw));

    [Fact]
    public void Release_addresses_are_built_from_the_tag_and_escape_it()
    {
        Assert.Equal($"{ProjectLocation.RepositoryUrl}/releases/tag/v1.0.0-rc.1", ReleaseCatalog.TagUrl("v1.0.0-rc.1"));
        Assert.Equal($"{ProjectLocation.RepositoryUrl}/releases/download/v1.0.0/Weir-win-Setup.exe", ReleaseCatalog.DownloadUrl("v1.0.0", "Weir-win-Setup.exe"));
        Assert.Equal($"{ProjectLocation.RepositoryUrl}/releases/download/v1%2F2/a%20b.exe", ReleaseCatalog.DownloadUrl("v1/2", "a b.exe"));
    }

    [Fact]
    public void A_version_becomes_its_tag_and_a_missing_one_is_refused()
    {
        Assert.Equal("v1.0.0", ReleaseCatalog.TagForVersion("1.0.0"));
        Assert.Equal("v1.0.0", ReleaseCatalog.TagForVersion("v1.0.0"));
        Assert.Equal("Release version is missing.", Assert.Throws<WireValueException>(() => ReleaseCatalog.TagForVersion(" ")).Message);
    }

    [Fact]
    public void A_release_is_read_with_every_field_GitHub_gives()
    {
        var asset = new WireObject()
            .Set("name", " Weir-win-Setup.exe ")
            .Set("url", "https://api.example/assets/1")
            .Set("browser_download_url", "https://example/Weir-win-Setup.exe")
            .Set("size", 1234L)
            .Set("content_type", "application/octet-stream");
        var payload = Payload("v1.2.0")
            .Set("name", "Weir 1.2.0")
            .Set("html_url", "https://example/releases/v1.2.0")
            .Set("published_at", "2026-10-01T08:30:00Z")
            .Set("draft", false)
            .Set("prerelease", true)
            .Set("assets", new WireArray([asset]));

        var release = ReleaseCatalog.CoerceReleasePayload(payload);

        Assert.Equal("v1.2.0", release.TagName);
        Assert.Equal("1.2.0", release.Version);
        Assert.Equal("Weir 1.2.0", release.ReleaseName);
        Assert.Equal("https://example/releases/v1.2.0", release.HtmlUrl);
        Assert.Equal("2026-10-01T08:30:00Z", release.PublishedAt!.Value.ToWireText());
        Assert.False(release.Draft);
        Assert.True(release.Prerelease);
        Assert.Equal(new GitHubReleaseAsset("Weir-win-Setup.exe", "https://api.example/assets/1", "https://example/Weir-win-Setup.exe", 1234, "application/octet-stream"), Assert.Single(release.Assets));
    }

    [Fact]
    public void A_release_with_only_a_tag_has_no_name_address_date_or_assets()
    {
        var release = ReleaseCatalog.CoerceReleasePayload(Payload().Set("published_at", "  ").Set("name", string.Empty));

        Assert.Null(release.ReleaseName);
        Assert.Null(release.HtmlUrl);
        Assert.Null(release.PublishedAt);
        Assert.Empty(release.Assets);
        Assert.False(release.Draft);
        Assert.False(release.Prerelease);
    }

    [Fact]
    public void An_asset_with_no_size_or_type_reads_as_zero_bytes_and_untyped_and_a_huge_size_is_capped()
    {
        var plain = new WireObject().Set("name", "a.zip").Set("url", "https://api.example/a");
        var huge = new WireObject().Set("name", "b.zip").Set("url", "https://api.example/b").Set("size", new WireInteger(BigInteger.Pow(10, 30)));

        var assets = ReleaseCatalog.CoerceReleasePayload(Payload().Set("assets", new WireArray([plain, huge, new WireString("not an asset")]))).Assets;

        Assert.Equal(2, assets.Count);
        Assert.Equal(0, assets[0].SizeBytes);
        Assert.Null(assets[0].ContentType);
        Assert.Equal(string.Empty, assets[0].BrowserDownloadUrl);
        Assert.Equal(long.MaxValue, assets[1].SizeBytes);
    }

    [Fact]
    public void A_payload_that_cannot_be_read_is_refused_in_plain_words()
    {
        const string Unexpected = "Release API returned an unexpected response.";

        Assert.Equal(Unexpected, Assert.Throws<WireValueException>(() => ReleaseCatalog.CoerceReleasePayload(new WireString("x"))).Message);
        Assert.Equal(Unexpected, Assert.Throws<WireValueException>(() => ReleaseCatalog.CoerceReleaseListPayload(new WireObject())).Message);
        Assert.Equal("Release metadata is missing a valid tag name.", Assert.Throws<WireValueException>(() => ReleaseCatalog.CoerceReleasePayload(new WireObject())).Message);
        Assert.Equal("Release metadata is missing a valid tag name.", Assert.Throws<WireValueException>(() => ReleaseCatalog.CoerceReleasePayload(Payload("v"))).Message);
        Assert.Equal("Invalid isoformat string: 'yesterday'", Assert.Throws<WireValueException>(() => ReleaseCatalog.CoerceReleasePayload(Payload().Set("published_at", "yesterday"))).Message);
        var incomplete = new WireObject().Set("name", "a.zip");
        Assert.Equal("Release asset metadata is incomplete.", Assert.Throws<WireValueException>(() => ReleaseCatalog.CoerceReleasePayload(Payload().Set("assets", new WireArray([incomplete])))).Message);
    }

    [Fact]
    public void The_status_of_a_failed_check_has_no_release_in_it_and_names_the_docker_image_only_for_docker()
    {
        var docker = UpdateStatus.Unavailable("1.0.0", "docker", "unavailable", "Could not check.");
        var windows = UpdateStatus.Unavailable("1.0.0", "windows", "unavailable", "Could not check.");
        var other = UpdateStatus.Unavailable("1.0.0", "source", "unavailable", "Could not check.");

        Assert.Equal(ProjectLocation.DockerImage, Text(docker, "docker_image"));
        Assert.IsType<WireNull>(windows["docker_image"]);
        Assert.True(Assert.IsType<WireBool>(windows["in_app_upgrade_supported"]).Value);
        Assert.False(Assert.IsType<WireBool>(other["in_app_upgrade_supported"]).Value);
        Assert.IsType<WireNull>(docker["latest_version"]);
        Assert.Equal("Could not check.", Text(docker, "summary"));
    }

    [Fact]
    public void A_newer_release_is_on_offer_with_the_way_to_install_it_for_each_kind_of_install()
    {
        var release = Release("1.2.0", Setup);

        var windows = UpdateStatus.FromRelease("1.1.0", "windows", release);
        var docker = UpdateStatus.FromRelease("1.1.0", "docker", release);

        Assert.Equal("update_available", Text(windows, "status"));
        Assert.Equal("Weir 1.2.0 is available.", Text(windows, "summary"));
        Assert.Equal("https://example/Weir-win-Setup.exe", Text(windows, "windows_installer_url"));
        Assert.Equal("Updates are managed by the Weir desktop app via Velopack.", Text(windows, "in_app_upgrade_summary"));
        Assert.IsType<WireNull>(windows["docker_update_command"]);
        Assert.Equal("docker compose pull && docker compose up -d", Text(docker, "docker_update_command"));
        Assert.Equal("1.2.0", Text(docker, "docker_tag"));
        Assert.IsType<WireNull>(docker["in_app_upgrade_summary"]);
    }

    [Fact]
    public void An_install_on_the_newest_release_or_ahead_of_it_is_up_to_date()
    {
        Assert.Equal("up_to_date", Text(UpdateStatus.FromRelease("1.2.0", "windows", Release()), "status"));
        Assert.Equal("This install is already on Weir 1.2.0.", Text(UpdateStatus.FromRelease("1.2.0", "windows", Release()), "summary"));
        Assert.Equal("up_to_date", Text(UpdateStatus.FromRelease("1.3.0", "windows", Release()), "status"));
        Assert.Equal("up_to_date", Text(UpdateStatus.FromRelease("not-a-version", "windows", Release()), "status"));
        Assert.Throws<ArgumentNullException>(() => UpdateStatus.FromRelease("1.0.0", "windows", null!));
    }

    [Fact]
    public void A_release_with_no_readable_version_is_reported_as_unavailable_with_no_docker_tag()
    {
        var blank = new GitHubReleaseRecord("v", string.Empty, null, null, null, false, false, []);

        var status = UpdateStatus.FromRelease("1.0.0", "docker", blank);

        Assert.Equal("unavailable", Text(status, "status"));
        Assert.Equal("Could not read the latest release version.", Text(status, "summary"));
        Assert.IsType<WireNull>(status["docker_tag"]);
        Assert.IsType<WireNull>(status["docker_update_command"]);
        Assert.IsType<WireNull>(status["published_at"]);
        Assert.Equal(string.Empty, Text(status, "latest_name"));
    }

    [Fact]
    public void A_rate_limited_check_keeps_the_release_it_knew_and_says_whether_it_was_newer()
    {
        var retryAt = new DateTimeOffset(2026, 10, 19, 23, 35, 0, TimeSpan.FromHours(2));
        var summary = UpdateStatus.RateLimitedSummary(retryAt, null);

        var newer = UpdateStatus.RateLimited("1.1.0", "windows", Release("1.2.0"), retryAt, summary);
        var older = UpdateStatus.RateLimited("1.2.0", "windows", Release("1.2.0"), retryAt, summary);
        var unknown = UpdateStatus.RateLimited("1.1.0", "windows", null, retryAt, summary);

        Assert.Equal("rate_limited", Text(newer, "status"));
        Assert.True(Assert.IsType<WireBool>(newer["known_update_available"]).Value);
        Assert.Equal("1.2.0", Text(newer, "latest_version"));
        Assert.False(Assert.IsType<WireBool>(older["known_update_available"]).Value);
        Assert.False(Assert.IsType<WireBool>(unknown["known_update_available"]).Value);
        Assert.IsType<WireNull>(unknown["latest_version"]);
        Assert.Equal("2026-10-19T21:35:00Z", Text(newer, "retry_at"));
        Assert.Equal(summary, Text(unknown, "summary"));
        Assert.Equal("GitHub is limiting update checks from your network right now. Weir will check again at 9:35 pm.", summary);
    }

    [Fact]
    public void The_check_again_time_is_told_on_the_clock_of_the_time_zone_given()
    {
        var at = new DateTimeOffset(2026, 10, 19, 12, 5, 0, TimeSpan.Zero);

        Assert.Equal("12:05 pm", UpdateStatus.ClockText(at, null));
        Assert.Equal("10:05 pm", UpdateStatus.ClockText(at, "Australia/Brisbane"));
        Assert.Equal("12:05 am", UpdateStatus.ClockText(new DateTimeOffset(2026, 10, 19, 0, 5, 0, TimeSpan.Zero), null));
    }

    [Fact]
    public void The_update_settings_have_a_default_and_a_notify_only_fallback()
    {
        Assert.Equal("Auto", Text(UpdateStatus.DefaultUpdateSettings, "mode"));
        Assert.Equal("NotifyOnly", Text(UpdateStatus.UnreadableUpdateSettings, "mode"));
        Assert.Equal(60, Assert.IsType<WireInteger>(UpdateStatus.DefaultUpdateSettings["check_interval_minutes"]).Value);
    }

    [Fact]
    public void A_new_install_and_a_file_that_cannot_be_read_check_for_nothing()
    {
        Assert.Equal(WireBool.False, UpdateStatus.DefaultUpdateSettings["check_for_updates"]);
        Assert.Equal(WireBool.False, UpdateStatus.UnreadableUpdateSettings["check_for_updates"]);
    }

    [Fact]
    public void A_file_saved_before_the_choice_existed_keeps_checking_and_a_saved_choice_is_followed()
    {
        var older = UpdateStatus.ParseUpdateSettings("""{"mode": "DownloadOnly", "checkOnStartup": false, "checkIntervalMinutes": 240}""")!;
        var off = UpdateStatus.ParseUpdateSettings("""{"mode": "Auto", "checkForUpdates": false}""")!;
        var on = UpdateStatus.ParseUpdateSettings("""{"mode": "Auto", "checkForUpdates": true}""")!;

        Assert.Equal(WireBool.True, older["check_for_updates"]);
        Assert.Equal(WireBool.False, off["check_for_updates"]);
        Assert.Equal(WireBool.True, on["check_for_updates"]);
    }

    [Fact]
    public void The_choice_to_check_is_written_for_the_tray_to_read()
    {
        var written = Assert.IsType<WireObject>(WireJsonParser.Parse(UpdateStatus.SerializeUpdateSettings("Auto", true, 60, false)));

        Assert.Equal(WireBool.False, written["checkForUpdates"]);
    }

    [Fact]
    public void Update_checking_that_is_off_is_a_status_of_its_own_with_no_release_in_it()
    {
        var status = UpdateStatus.Off("3.2.16", "windows");

        Assert.Equal("off", Text(status, "status"));
        Assert.Equal("Update checking is off. Turn it on below to look for new versions of Weir.", Text(status, "summary"));
        Assert.IsType<WireNull>(status["latest_version"]);
        Assert.Equal(WireBool.True, status["in_app_upgrade_supported"]);
    }

    [Fact]
    public void A_settings_file_is_read_with_its_defaults_filled_in()
    {
        var full = UpdateStatus.ParseUpdateSettings("""{"mode": " DownloadOnly ", "checkOnStartup": false, "checkIntervalMinutes": 120}""");
        var minimal = UpdateStatus.ParseUpdateSettings("""{"mode": "Auto"}""");

        Assert.Equal("DownloadOnly", Text(full!, "mode"));
        Assert.False(Assert.IsType<WireBool>(full!["check_on_startup"]).Value);
        Assert.Equal(120, Assert.IsType<WireInteger>(full["check_interval_minutes"]).Value);
        Assert.True(Assert.IsType<WireBool>(minimal!["check_on_startup"]).Value);
        Assert.Equal(60, Assert.IsType<WireInteger>(minimal["check_interval_minutes"]).Value);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"mode": "Sometimes"}""")]
    [InlineData("""{"mode": "Auto", "checkIntervalMinutes": "often"}""")]
    [InlineData("""{"mode": "Auto", "checkIntervalMinutes": []}""")]
    [InlineData("""{"mode": "Auto", "checkIntervalMinutes": 0}""")]
    [InlineData("""{"mode": "Auto", "checkIntervalMinutes": 10081}""")]
    public void A_settings_file_that_cannot_be_used_is_not_read_so_the_fallback_applies(string text) =>
        Assert.Null(UpdateStatus.ParseUpdateSettings(text));

    [Fact]
    public void The_interval_limits_are_one_minute_and_one_week()
    {
        Assert.NotNull(UpdateStatus.ParseUpdateSettings("""{"mode": "Auto", "checkIntervalMinutes": 1}"""));
        Assert.NotNull(UpdateStatus.ParseUpdateSettings("""{"mode": "Auto", "checkIntervalMinutes": 10080}"""));
    }

    [Fact]
    public void A_settings_file_that_is_json_but_not_an_object_is_a_wrong_type()
    {
        Assert.Throws<WireTypeException>(() => UpdateStatus.ParseUpdateSettings("[1, 2]"));
    }

    [Fact]
    public void The_files_the_tray_reads_are_written_as_json()
    {
        var settings = UpdateStatus.SerializeUpdateSettings("Auto", true, 60, true);
        var work = UpdateStatus.SerializeWorkState(true, new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal("Auto", Text(Assert.IsType<WireObject>(WireJsonParser.Parse(settings)), "mode"));
        Assert.Contains("\n  \"mode\"", settings, StringComparison.Ordinal);
        var state = Assert.IsType<WireObject>(WireJsonParser.Parse(work));
        Assert.True(Assert.IsType<WireBool>(state["busy"]).Value);
        Assert.Equal("2026-10-19T10:00:00.0000000Z", Text(state, "checkedAt"));
        Assert.DoesNotContain('\n', work);
    }

    [Fact]
    public void The_tray_state_reads_the_version_it_found_and_ignores_one_that_is_not_text()
    {
        var found = UpdateStatus.ParseUpdateState("""{"version": "1.2.0", "state": "idle"}""");
        var blank = UpdateStatus.ParseUpdateState("""{"version": "", "state": "checking"}""");
        var odd = UpdateStatus.ParseUpdateState("""{"version": 12, "state": "downloading", "downloaded": true}""");

        Assert.Equal("1.2.0", Text(found, "pending_version"));
        Assert.IsType<WireNull>(blank["pending_version"]);
        Assert.Equal("checking", Text(blank, "state"));
        Assert.Equal("idle", Text(odd, "state"));
        Assert.False(Assert.IsType<WireBool>(odd["downloaded"]).Value);
    }

    [Fact]
    public void A_state_file_that_is_not_json_or_not_an_object_says_nothing_is_happening()
    {
        Assert.Equal("idle", Text(UpdateStatus.ParseUpdateState("{broken"), "state"));
        Assert.Equal("idle", Text(UpdateStatus.ParseUpdateState("[1]"), "state"));
        Assert.Equal("idle", Text(UpdateStatus.ParseUpdateState("""{"state": "somewhere else"}"""), "state"));
    }

    [Fact]
    public void A_step_a_person_asked_for_replaces_the_state_and_keeps_only_the_version_found()
    {
        var read = UpdateStatus.ParseUpdateState("""{"version": "1.2.0", "state": "failed", "failure": "disk full", "downloaded": true}""");

        var stepped = UpdateStatus.WithStep(read, UpdateStatus.StateDownloading);
        var bare = UpdateStatus.WithStep(UpdateStatus.ParseUpdateState(null), UpdateStatus.StateChecking);

        Assert.Equal("downloading", Text(stepped, "state"));
        Assert.Equal("1.2.0", Text(stepped, "pending_version"));
        Assert.False(Assert.IsType<WireBool>(stepped["downloaded"]).Value);
        Assert.IsType<WireNull>(stepped["failure"]);
        Assert.IsType<WireNull>(bare["pending_version"]);
        Assert.Throws<ArgumentNullException>(() => UpdateStatus.WithStep(null!, UpdateStatus.StateIdle));
    }
}
