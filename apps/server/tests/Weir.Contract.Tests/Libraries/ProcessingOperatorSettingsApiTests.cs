using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.Libraries.LibrariesPartAHelpers;

namespace Weir.Contract.Tests.Libraries;

/// <summary>Processing operator settings: the periodic-scan switch per media type, retired fields, validation, files at once and the resolution budget.</summary>
[ContractArea("libraries")]
public sealed class ProcessingOperatorSettingsApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Path = Api + "/processing/operator-settings";

    private static readonly string[] FilesAtOnceFields =
    [
        "files_at_once",
        "worker_slots",
        "effective_files_at_once",
        "running",
        "waiting",
        "waiting_for",
        "message",
        "slots_note",
    ];

    /// <summary>The hours window Movies and TV once had: nothing acts on it, and each workflow's weekly schedule decides when it works.</summary>
    private static readonly string[] HoursWindowFields =
    [
        "movie_schedule_hours_limited",
        "movie_schedule_days",
        "movie_schedule_start",
        "movie_schedule_end",
        "tv_schedule_hours_limited",
        "tv_schedule_days",
        "tv_schedule_start",
        "tv_schedule_end",
    ];

    [Fact]
    public async Task Operator_settings_get_shape()
    {
        // A fresh install, so the defaults are not disturbed by the PUT tests in this module.
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();
        var suite = await admin.GetAsync($"{Api}/suite/settings");
        suite.ShouldBe(HttpStatusCode.OK);
        var expectedScheduleTimezone = (string)suite.Fields["app_timezone"]!;
        var response = await admin.GetAsync(Path);
        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;
        Assert.Equal(1, (int)body["max_concurrent_files"]!);
        // The wait, the minimum size and the space to keep free are each workflow's, and a file of unknown resolution costs
        // what a 1080p file does.
        foreach (var retired in new[] { "min_file_age_seconds", "min_input_file_size_mb" })
        {
            Assert.False(body.ContainsKey(retired), retired);
        }

        Assert.False(body.ContainsKey("minimum_free_disk_space_mb"));
        Assert.False(body.ContainsKey("runner_cost_undetermined"));
        Assert.True((bool)body["movie_schedule_enabled"]!);
        Assert.False(body.ContainsKey("movie_schedule_interval_seconds"));
        Assert.True((bool)body["tv_schedule_enabled"]!);
        Assert.False(body.ContainsKey("tv_schedule_interval_seconds"));
        Assert.All(HoursWindowFields, field => Assert.False(body.ContainsKey(field), field));
        Assert.Equal(expectedScheduleTimezone, (string)body["schedule_timezone"]!);
    }

    [Fact]
    public async Task Operator_settings_put_updates()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var response = await admin.PutWithCsrfAsync(
            Path,
            Obj(
                ("max_concurrent_files", 4),
                ("runner_capacity", 6),
                ("movie_schedule_enabled", true),
                ("tv_schedule_enabled", false)));
        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;
        Assert.Equal(4, (int)body["max_concurrent_files"]!);
        Assert.Equal(6, (int)body["runner_capacity"]!);
        Assert.True((bool)body["movie_schedule_enabled"]!);
        Assert.False((bool)body["tv_schedule_enabled"]!);
        // And the saved values are what a later read returns.
        var again = (await admin.GetAsync(Path)).Fields;
        Assert.Equal(4, (int)again["max_concurrent_files"]!);
        Assert.False((bool)again["tv_schedule_enabled"]!);
    }

    [Fact]
    public async Task Operator_settings_put_tv_only_preserves_movie()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        (await admin.PutWithCsrfAsync(Path, Obj(("movie_schedule_enabled", false), ("tv_schedule_enabled", true)))).ShouldBe(HttpStatusCode.OK);

        var second = await admin.PutWithCsrfAsync(Path, Obj(("tv_schedule_enabled", false)));

        second.ShouldBe(HttpStatusCode.OK);
        Assert.False((bool)second.Fields["movie_schedule_enabled"]!);
        Assert.False((bool)second.Fields["tv_schedule_enabled"]!);
    }

    [Fact]
    public async Task Operator_settings_put_process_only_preserves_the_switches()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        (await admin.PutWithCsrfAsync(Path, Obj(("movie_schedule_enabled", true), ("tv_schedule_enabled", false)))).ShouldBe(HttpStatusCode.OK);

        var response = await admin.PutWithCsrfAsync(Path, Obj(("max_concurrent_files", 3), ("runner_capacity", 8)));

        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;
        Assert.Equal(3, (int)body["max_concurrent_files"]!);
        Assert.Equal(8, (int)body["runner_capacity"]!);
        Assert.True((bool)body["movie_schedule_enabled"]!);
        Assert.False((bool)body["tv_schedule_enabled"]!);
    }

    [Fact]
    public async Task An_older_client_sending_the_whole_hours_window_is_answered_and_only_the_switch_is_kept()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        (await admin.PutWithCsrfAsync(Path, Obj(("movie_schedule_enabled", true), ("tv_schedule_enabled", true)))).ShouldBe(HttpStatusCode.OK);

        var response = await admin.PutWithCsrfAsync(
            Path,
            Obj(
                ("movie_schedule_enabled", false),
                ("movie_schedule_hours_limited", true),
                ("movie_schedule_days", "Mon,Tue"),
                ("movie_schedule_start", "09:00"),
                ("movie_schedule_end", "17:30"),
                ("tv_schedule_enabled", true),
                ("tv_schedule_hours_limited", true),
                ("tv_schedule_days", "Wed"),
                ("tv_schedule_start", "08:00"),
                ("tv_schedule_end", "09:30")));

        response.ShouldBe(HttpStatusCode.OK);
        Assert.False((bool)response.Fields["movie_schedule_enabled"]!);
        Assert.True((bool)response.Fields["tv_schedule_enabled"]!);
        Assert.All(HoursWindowFields, field => Assert.False(response.Fields.ContainsKey(field), field));
        var again = (await admin.GetAsync(Path)).Fields;
        Assert.All(HoursWindowFields, field => Assert.False(again.ContainsKey(field), field));
        // Put the switch back, for the tests that share this server.
        (await admin.PutWithCsrfAsync(Path, Obj(("movie_schedule_enabled", true)))).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_older_client_sending_only_hours_window_fields_is_answered_and_they_are_not_read()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var before = (await admin.GetAsync(Path)).Fields;

        // Not a whole group, and values that were once refused: a day that is not a day, an hour past 23, a number for a time.
        var response = await admin.PutWithCsrfAsync(
            Path,
            Obj(("movie_schedule_hours_limited", true), ("movie_schedule_days", "Caturday"), ("tv_schedule_start", "25:00"), ("tv_schedule_end", 9)));

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(before.ToJsonString(), response.Fields.ToJsonString());
    }

    [Fact]
    public async Task Operator_settings_put_rejects_csrf_only()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        (await admin.PutWithCsrfAsync(Path, new JsonObject())).ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Files_at_once_goes_to_ten_and_no_further()
    {
        // #633: one clear choice, 1 to 10.
        using var admin = await fixture.Server.CreateAdminClientAsync();

        var ok = await admin.PutWithCsrfAsync(Path, Obj(("max_concurrent_files", 10)));
        ok.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(10, (int)ok.Fields["max_concurrent_files"]!);
        var tooMany = await admin.PutWithCsrfAsync(Path, Obj(("max_concurrent_files", 11)));
        tooMany.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Resolution_budget_is_a_switch_that_starts_off()
    {
        // #633: off, a file needs only a free slot, so "Files at once" means what it says.
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();

        Assert.False((bool)(await admin.GetAsync(Path)).Fields["runner_budget_enabled"]!);
        var response = await admin.PutWithCsrfAsync(Path, Obj(("runner_budget_enabled", true)));
        response.ShouldBe(HttpStatusCode.OK);
        Assert.True((bool)response.Fields["runner_budget_enabled"]!);
    }

    [Fact]
    public async Task Files_at_once_read_out_shape()
    {
        // #633: what is running, what is waiting, and the one limit the waiting files are waiting on.
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();

        var response = await admin.GetAsync($"{Api}/processing/files-at-once");

        response.ShouldBe(HttpStatusCode.OK);
        var body = response.Fields;
        Assert.Equal(FilesAtOnceFields, body.Select(pair => pair.Key));
        Assert.Equal(1, (int)body["files_at_once"]!);
        Assert.Equal(0, (int)body["waiting"]!);
        Assert.Equal("nothing", (string)body["waiting_for"]!);
        Assert.Equal("", (string)body["message"]!);
    }

    [Fact]
    public async Task An_older_client_sending_the_wait_and_minimum_size_is_answered_and_they_are_ignored()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();

        var response = await admin.PutWithCsrfAsync(Path, Obj(("min_file_age_seconds", 120), ("min_input_file_size_mb", 200)));

        response.ShouldBe(HttpStatusCode.OK);
        Assert.False(response.Fields.ContainsKey("min_file_age_seconds"));
        Assert.False(response.Fields.ContainsKey("min_input_file_size_mb"));
    }
}
