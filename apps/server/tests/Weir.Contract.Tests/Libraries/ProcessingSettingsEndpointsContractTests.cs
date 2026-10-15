using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weir.Contract.FakeTools;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using static Weir.Contract.Tests.Libraries.LibrariesPartBChecks;

namespace Weir.Contract.Tests.Libraries;

/// <summary>
/// Basic contract coverage for hardware, maintenance, rule sets, reject support, the metadata provider and Direct Play
/// devices: auth, viewer versus operator, status codes, response keys, round-trips.
/// </summary>
[ContractArea("libraries")]
public sealed partial class ProcessingSettingsEndpointsContractTests(LibrariesPartBViewerFixture fixture)
    : IClassFixture<LibrariesPartBViewerFixture>
{
    private const string Api = WeirClient.Api;
    private const string Maintenance = $"{Api}/processing/maintenance";

    private WeirServer Server => fixture.Server;

    // --- hardware ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hardware_check_ffmpeg_was_too_slow_to_answer_says_so_and_the_next_check_asks_again()
    {
        using var tools = FakeFfmpeg.Install();
        // The first run of the tool takes longer than the 10 seconds the check allows; the next is quick.
        tools.SetScript(new JsonObject { ["default"] = new JsonObject { [FakeToolProtocol.HwaccelsFirstCallDelaySecondsKey] = 12 } });
        await tools.WarmUpAsync();
        await using var server = await WeirServer.StartNewAsync(tools.Env);
        using var admin = await server.CreateAdminClientAsync();

        var tooSlow = await admin.GetAsync($"{Api}/processing/hardware");
        await HardwareReport.OnceFfmpegAnswersAsync(admin);

        Status(tooSlow, HttpStatusCode.OK);
        Assert.False((bool)tooSlow.Fields["detected"]!, tooSlow.ToString());
        Assert.Contains("timed out after 10.0 seconds", (string)tooSlow.Fields["detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hardware_report_from_the_configured_ffmpeg()
    {
        using var tools = FakeFfmpeg.Install();
        await tools.WarmUpAsync();
        await using var server = await WeirServer.StartNewAsync(tools.Env);
        using (var anonymous = server.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Api}/processing/hardware")).Status);
        }

        using var admin = await server.CreateAdminClientAsync();
        var response = await HardwareReport.OnceFfmpegAnswersAsync(admin);

        var body = response.Fields;
        HasKeys(body, "detected", "available_methods", "vendors", "selectable_vendors", "strictness_levels", "detail");
        // The fake ffmpeg answers but lists no acceleration methods: asked successfully, nothing on offer.
        Assert.True((bool)body["detected"]!, response.ToString());
        Assert.Empty(body["available_methods"]!.AsArray());
        Assert.Empty(body["vendors"]!.AsArray());
        var selectable = Strings(body["selectable_vendors"]);
        Assert.Equal(selectable.Order(StringComparer.Ordinal), selectable);
        Assert.NotEmpty(selectable);
        Assert.NotEmpty(body["strictness_levels"]!.AsArray());
        Assert.NotEmpty((string)body["detail"]!);
        Assert.Contains(tools.Calls(tool: "ffmpeg"), call => call.Arguments.Contains("-hwaccels"));
    }

    // --- maintenance ------------------------------------------------------------------------------------

    [Fact]
    public async Task Maintenance_state_shape()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);

        var response = await viewer.GetAsync(Maintenance);

        Status(response, HttpStatusCode.OK);
        var families = response.Fields["families"]!.AsArray();
        Assert.Equal(["work_temp_stale_sweep", "unclaimed_handbacks"], families.Select(family => (string)family!["family"]!));
        foreach (var node in families)
        {
            var family = node!.AsObject();
            HasKeys(
                family, "family", "enabled", "description", "pending", "running", "last_completed_at", "last_failed_at", "last_error");
            Assert.True(family["enabled"]!.GetValueKind() is JsonValueKind.True or JsonValueKind.False);
            Assert.True(family["pending"] is JsonValue pending && pending.TryGetValue<long>(out _));
        }
    }

    [Fact]
    public async Task Maintenance_run_needs_an_operator()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(Server);
        var body = new JsonObject { ["family"] = "work_temp_stale_sweep", ["media_scope"] = "tv" };

        using var anonymous = Server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostWithCsrfAsync($"{Maintenance}/run", Copy(body))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostWithCsrfAsync($"{Maintenance}/run", Copy(body))).Status);
    }

    [Fact]
    public async Task Maintenance_unclaimed_handback_cleanup_can_be_run_by_hand()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.PostWithCsrfAsync(
            $"{Maintenance}/run", new JsonObject { ["family"] = "unclaimed_handbacks", ["media_scope"] = "tv" });

        Status(response, HttpStatusCode.OK);
        Assert.True((bool)response.Fields["queued"]!);
        Assert.NotEmpty((string)response.Fields["detail"]!);
    }

    [Theory]
    [InlineData("work_temp_stale_sweep", "processing.work_temp_stale_sweep.v1")]
    [InlineData("unclaimed_handbacks", "processing.unclaimed_handback_cleanup.v1")]
    public async Task Maintenance_run_now_runs_every_time_it_is_pressed(string family, string jobKind)
    {
        // Workers on, so each run is carried out, and the cleanup timers off, so only the presses below queue a run.
        await using var server = await WeirServer.StartNewAsync(new Dictionary<string, string> { ["WEIR_PROCESSING_WORKER_COUNT"] = "1" });
        using var admin = await server.CreateAdminClientAsync();
        var before = await CompletedRunsAsync(admin, jobKind);
        foreach (var run in new[] { 1, 2 })
        {
            var pressed = await admin.PostWithCsrfAsync(
                $"{Maintenance}/run", new JsonObject { ["family"] = family, ["media_scope"] = "movie" });
            Status(pressed, HttpStatusCode.OK);
            await Poll.UntilAsync(async () => await CompletedRunsAsync(admin, jobKind) >= before + run, $"run {run} of {family} to finish");
        }
    }

    [Fact]
    public async Task Maintenance_has_no_failed_download_cleanup_any_more()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.PostWithCsrfAsync(
            $"{Maintenance}/run", new JsonObject { ["family"] = "failure_cleanup", ["media_scope"] = "tv" });

        Status(response, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_failed_download_cleanup_settings_are_accepted_ignored_and_no_longer_reported()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.PutWithCsrfAsync(
            $"{Api}/processing/operator-settings",
            new JsonObject { ["failure_cleanup_enabled"] = true, ["failure_cleanup_interval_seconds"] = 3600 });

        Status(response, HttpStatusCode.OK);
        Assert.False(response.Fields.ContainsKey("failure_cleanup_enabled"));
        Assert.False(response.Fields.ContainsKey("failure_cleanup_interval_seconds"));
    }

    [Fact]
    public async Task Maintenance_run_rejects_an_unknown_scope()
    {
        using var admin = await Server.CreateAdminClientAsync();

        var response = await admin.PostWithCsrfAsync(
            $"{Maintenance}/run", new JsonObject { ["family"] = "work_temp_stale_sweep", ["media_scope"] = "music" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.Status);
    }

    private static async Task<int> CompletedRunsAsync(WeirClient admin, string jobKind)
    {
        var response = await admin.GetAsync($"{Api}/processing/jobs/inspection", ("status", "completed"), ("limit", 100));
        Status(response, HttpStatusCode.OK);
        return response.Fields["jobs"]!.AsArray().Count(job => (string)job!["job_kind"]! == jobKind);
    }

    // A body is sent once; each request gets its own copy.
    private static JsonObject Copy(JsonObject body) => (JsonObject)body.DeepClone();
}
