using Weir.Core.Json;
using Weir.Core.Updates;

namespace Weir.Core.Tests.Updates;

public sealed class UpdateStatusTests
{
    private static GitHubReleaseRecord Release(string version) =>
        new($"v{version}", version, null, null, null, false, version.Contains('-', StringComparison.Ordinal), []);

    private static string StatusOf(string running, string released) =>
        ((WireString)UpdateStatus.FromRelease(running, "windows", Release(released))["status"]).Value;

    [Theory]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.9", "1.0.0-rc.10")]
    [InlineData("1.0.0-rc.9", "1.0.0")]
    [InlineData("3.2.16", "4.0.0")]
    public void A_newer_release_is_an_update(string running, string released) =>
        Assert.Equal("update_available", StatusOf(running, released));

    [Theory]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    [InlineData("1.0.0", "1.0.0-rc.9")]
    [InlineData("1.0.0", "1.0.0")]
    public void A_release_that_is_not_newer_is_not_an_update(string running, string released) =>
        Assert.Equal("up_to_date", StatusOf(running, released));

    [Fact]
    public void A_pre_release_docker_install_is_pointed_at_the_pre_release_tag()
    {
        var status = UpdateStatus.FromRelease("1.0.0-rc.1", "docker", Release("1.0.0-rc.2"));

        Assert.Equal("1.0.0-rc.2", ((WireString)status["docker_tag"]).Value);
    }

    [Fact]
    public void The_release_list_payload_reads_every_release_including_pre_releases_and_drafts()
    {
        var payload = WireJsonParser.Parse(
            """[{"tag_name":"v1.0.0-rc.2","prerelease":true,"draft":false},{"tag_name":"v1.0.0-rc.1","prerelease":true,"draft":true}]""");

        var releases = ReleaseCatalog.CoerceReleaseListPayload(payload);

        Assert.Equal(["1.0.0-rc.2", "1.0.0-rc.1"], releases.Select(release => release.Version));
        Assert.Equal([true, true], releases.Select(release => release.Prerelease));
        Assert.Equal([false, true], releases.Select(release => release.Draft));
    }

    [Fact]
    public void A_release_list_payload_that_is_not_a_list_is_refused()
    {
        var payload = WireJsonParser.Parse("""{"message":"Not Found"}""");

        Assert.Throws<WireValueException>(() => ReleaseCatalog.CoerceReleaseListPayload(payload));
    }

    private static string StateOf(string? file) =>
        WireJsonWriter.Dumps(UpdateStatus.ParseUpdateState(file), WireJsonFormat.Response);

    [Fact]
    public void The_tray_state_names_where_the_tray_is_and_why_it_failed()
    {
        Assert.Equal("{\"downloaded\":false,\"pending_version\":null,\"state\":\"checking\",\"failure\":null}", StateOf("""{"state":"checking","downloaded":false}"""));
        Assert.Equal("{\"downloaded\":false,\"pending_version\":\"2.0.0\",\"state\":\"downloading\",\"failure\":null}", StateOf("""{"state":"downloading","version":"2.0.0"}"""));
        Assert.Equal("{\"downloaded\":false,\"pending_version\":null,\"state\":\"failed\",\"failure\":\"No route.\"}", StateOf("""{"state":"failed","failure":"No route."}"""));
    }

    [Fact]
    public void The_tray_state_says_when_the_last_check_was_made_whether_or_not_it_found_a_newer_version()
    {
        Assert.Equal(
            "{\"downloaded\":false,\"pending_version\":null,\"state\":\"idle\",\"failure\":null,\"last_checked_at\":\"2026-10-21T09:30:00Z\"}",
            StateOf("""{"state":"idle","downloaded":false,"checkedAt":"2026-10-21T09:30:00Z"}"""));
        Assert.Equal(
            "{\"downloaded\":false,\"pending_version\":\"2.0.0\",\"state\":\"idle\",\"failure\":null,\"last_checked_at\":\"2026-10-21T09:30:00Z\"}",
            StateOf("""{"state":"idle","version":"2.0.0","checkedAt":"2026-10-21T10:30:00+01:00"}"""));
    }

    [Theory]
    [InlineData("""{"state":"idle","checkedAt":"yesterday"}""")]
    [InlineData("""{"state":"idle","checkedAt":12}""")]
    [InlineData("""{"state":"idle","checkedAt":null}""")]
    [InlineData("""{"state":"idle"}""")]
    public void A_time_of_the_last_check_that_is_missing_or_cannot_be_read_is_left_out(string file)
    {
        Assert.Equal("{\"downloaded\":false,\"pending_version\":null,\"state\":\"idle\",\"failure\":null}", StateOf(file));
    }

    [Fact]
    public void A_step_a_person_asked_for_keeps_the_time_of_the_last_check()
    {
        var read = UpdateStatus.ParseUpdateState("""{"state":"idle","checkedAt":"2026-10-21T09:30:00Z"}""");

        var stepped = UpdateStatus.WithStep(read, UpdateStatus.StateChecking);

        Assert.Equal("2026-10-21T09:30:00Z", Assert.IsType<WireString>(stepped["last_checked_at"]).Value);
    }

    [Fact]
    public void A_failure_is_only_read_from_a_failed_state()
    {
        Assert.Equal("{\"downloaded\":false,\"pending_version\":null,\"state\":\"idle\",\"failure\":null}", StateOf("""{"state":"idle","failure":"stale"}"""));
    }

    [Fact]
    public void Downloaded_settles_whether_an_update_waits_whatever_the_named_state_says()
    {
        Assert.Equal("{\"downloaded\":true,\"pending_version\":\"2.0.0\",\"state\":\"downloaded\",\"failure\":null}", StateOf("""{"state":"checking","downloaded":true,"version":"2.0.0"}"""));
        Assert.Equal("{\"downloaded\":false,\"pending_version\":\"2.0.0\",\"state\":\"idle\",\"failure\":null}", StateOf("""{"state":"downloaded","downloaded":false,"version":"2.0.0"}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1, 2]")]
    [InlineData("{\"state\": \"exploding\"}")]
    public void A_state_that_is_missing_or_not_one_of_the_tray_s_reads_as_idle(string? file) =>
        Assert.Equal("{\"downloaded\":false,\"pending_version\":null,\"state\":\"idle\",\"failure\":null}", StateOf(file));
}
