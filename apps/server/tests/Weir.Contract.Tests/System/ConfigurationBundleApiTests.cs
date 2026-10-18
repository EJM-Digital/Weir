using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>The configuration bundle (<c>/suite/configuration-bundle</c>): access, its one address, and round trips.</summary>
[ContractArea("system")]
public sealed class ConfigurationBundleApiTests(UsersFixture fixture) : IClassFixture<UsersFixture>
{
    private const string Bundle = $"{WeirClient.Api}/suite/configuration-bundle";

    private static readonly string[] HoursWindowFields =
    [
        "movie_schedule_hours_limited", "movie_schedule_days", "movie_schedule_start", "movie_schedule_end",
        "tv_schedule_hours_limited", "tv_schedule_days", "tv_schedule_start", "tv_schedule_end",
    ];

    [Fact]
    public async Task Configuration_bundle_get_requires_operator()
    {
        using var viewer = await SeededAccounts.SignInViewerAsync(fixture.Server);
        var response = await viewer.GetAsync(Bundle);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    /// <summary>
    /// One address per handler: <c>/suite/settings/configuration-bundle</c> and the <c>/system/...</c> bundle
    /// and backup paths are not served, so nothing comes to depend on a second address.
    /// </summary>
    [Fact]
    public async Task The_retired_bundle_url_aliases_are_not_served()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        foreach (var path in new[]
        {
            $"{WeirClient.Api}/suite/settings/configuration-bundle",
            $"{WeirClient.Api}/system/suite-configuration-bundle",
            $"{WeirClient.Api}/system/suite-configuration-backups",
        })
        {
            Assert.True((await admin.GetAsync(path)).Status == HttpStatusCode.NotFound, path);
        }
    }

    [Fact]
    public async Task Configuration_bundle_round_trip_ignores_the_product_name()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var r0 = await admin.GetAsync(Bundle);
        Assert.True(r0.Status == HttpStatusCode.OK, r0.ToString());
        var bundle = r0.Fields;
        Assert.Equal(4, (int)bundle["format_version"]!);
        Assert.True(bundle.ContainsKey("suite_settings"));
        Assert.True(bundle.ContainsKey("arr_library_operator_settings"));

        var changed = bundle.DeepClone().AsObject();
        changed["suite_settings"]!["product_display_name"] = "Bundle Restore Test";

        var put = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = changed });
        Assert.True(put.Status == HttpStatusCode.OK, put.ToString());
        // Weir is named after its machine, so a backup's product name is read and dropped.
        Assert.Equal(
            (string?)bundle["suite_settings"]!["product_display_name"],
            (string?)put.Fields["suite_settings"]!["product_display_name"]);

        var restore = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = bundle.DeepClone() });
        Assert.True(restore.Status == HttpStatusCode.OK, restore.ToString());
    }

    /// <summary>A restore replaces the backup schedule with the one the backup held: how often and at what time of day, not only whether it is on.</summary>
    [Fact]
    public async Task A_restore_puts_back_the_backup_schedule_the_backup_held()
    {
        await using var server = await WeirServer.StartNewAsync();
        using var admin = await server.CreateAdminClientAsync();
        const string Settings = $"{WeirClient.Api}/suite/settings";
        static JsonObject Schedule(int hours, string time) => new()
        {
            ["app_timezone"] = "UTC",
            ["log_retention_days"] = 30,
            ["configuration_backup_interval_hours"] = hours,
            ["configuration_backup_preferred_time"] = time,
        };

        Assert.Equal(HttpStatusCode.OK, (await admin.PutWithCsrfAsync(Settings, Schedule(48, "03:30"))).Status);
        var held = (await admin.GetAsync(Bundle)).Fields;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutWithCsrfAsync(Settings, Schedule(6, "04:45"))).Status);

        var restore = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = held.DeepClone() });

        Assert.True(restore.Status == HttpStatusCode.OK, restore.ToString());
        var after = (await admin.GetAsync(Settings)).Fields;
        Assert.Equal(48, (int)after["configuration_backup_interval_hours"]!);
        Assert.Equal("03:30", (string?)after["configuration_backup_preferred_time"]);
    }

    /// <summary>Pruner moved to Deluno (#473): exports drop its sections, older backups still restore.</summary>
    [Fact]
    public async Task Configuration_bundle_no_longer_carries_pruner_and_ignores_it_on_restore()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var r0 = await admin.GetAsync(Bundle);
        Assert.True(r0.Status == HttpStatusCode.OK, r0.ToString());
        var bundle = r0.Fields;
        Assert.DoesNotContain(bundle.Select(field => field.Key), key => key.StartsWith("pruner_", StringComparison.Ordinal));

        var older = bundle.DeepClone().AsObject();
        older["suite_settings"]!["product_display_name"] = "Restored From Older Backup";
        older["pruner_server_instances"] = new JsonArray(new JsonObject
        {
            ["id"] = 1,
            ["provider"] = "plex",
            ["display_name"] = "Living room",
        });
        older["pruner_scope_settings"] = new JsonArray(new JsonObject
        {
            ["id"] = 1,
            ["server_instance_id"] = 1,
            ["media_scope"] = "movies",
        });

        var put = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = older });
        Assert.True(put.Status == HttpStatusCode.OK, put.ToString());
        Assert.Equal(
            (string?)bundle["suite_settings"]!["product_display_name"],
            (string?)put.Fields["suite_settings"]!["product_display_name"]);

        var restore = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = bundle.DeepClone() });
        Assert.True(restore.Status == HttpStatusCode.OK, restore.ToString());
    }

    /// <summary>
    /// The signed-in home note and the hours window for Movies and for TV are retired: exports drop them, and a backup made
    /// before they were retired still restores, whatever it holds in them, with only the two periodic-scan switches kept.
    /// </summary>
    [Fact]
    public async Task A_backup_that_still_carries_the_retired_settings_restores_and_keeps_none_of_them()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var exported = await admin.GetAsync(Bundle);
        Assert.True(exported.Status == HttpStatusCode.OK, exported.ToString());
        var current = exported.Fields;
        Assert.False(current["suite_settings"]!.AsObject().ContainsKey("signed_in_home_notice"));
        Assert.All(HoursWindowFields, field => Assert.False(current["operator_settings"]!.AsObject().ContainsKey(field), field));

        var older = current.DeepClone().AsObject();
        older["suite_settings"]!["signed_in_home_notice"] = "Welcome back.";
        var operatorSettings = older["operator_settings"]!.AsObject();
        operatorSettings["movie_schedule_enabled"] = false;
        operatorSettings["movie_schedule_hours_limited"] = true;
        operatorSettings["movie_schedule_days"] = "Mon,Tue";
        operatorSettings["movie_schedule_start"] = "09:00";
        operatorSettings["movie_schedule_end"] = "17:30";
        operatorSettings["tv_schedule_hours_limited"] = true;
        operatorSettings["tv_schedule_days"] = "Wed";
        operatorSettings["tv_schedule_start"] = "08:00";
        operatorSettings["tv_schedule_end"] = "09:30";

        try
        {
            var put = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = older.DeepClone() });
            Assert.True(put.Status == HttpStatusCode.OK, put.ToString());
            Assert.False(put.Fields["suite_settings"]!.AsObject().ContainsKey("signed_in_home_notice"));
            Assert.All(HoursWindowFields, field => Assert.False(put.Fields["operator_settings"]!.AsObject().ContainsKey(field), field));
            Assert.Equal(0, (int)put.Fields["operator_settings"]!["movie_schedule_enabled"]!);

            // A note that is not even text was a server error to restore; it is not read now.
            older["suite_settings"]!["signed_in_home_notice"] = 7;
            var notText = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = older.DeepClone() });
            Assert.True(notText.Status == HttpStatusCode.OK, notText.ToString());

            var settings = await admin.GetAsync($"{WeirClient.Api}/suite/settings");
            Assert.False(settings.Fields.ContainsKey("signed_in_home_notice"));
            var performance = await admin.GetAsync($"{WeirClient.Api}/processing/operator-settings");
            Assert.All(HoursWindowFields, field => Assert.False(performance.Fields.ContainsKey(field), field));
            Assert.False((bool)performance.Fields["movie_schedule_enabled"]!);
        }
        finally
        {
            var restore = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = current.DeepClone() });
            Assert.True(restore.Status == HttpStatusCode.OK, restore.ToString());
        }
    }

    [Fact]
    public async Task Configuration_bundle_put_rejects_bad_version()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var r0 = await admin.GetAsync(Bundle);
        Assert.Equal(HttpStatusCode.OK, r0.Status);
        var bundle = r0.Fields;
        bundle["format_version"] = 999;

        var put = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = bundle.DeepClone() });
        Assert.Equal(HttpStatusCode.BadRequest, put.Status);
    }

    /// <summary>
    /// A bundle whose library rows would fail POST /processing/libraries's own checks is refused as a
    /// whole, naming the offending library, and leaves the library table untouched (#723).
    /// </summary>
    [Fact]
    public async Task Configuration_bundle_restore_validates_every_library_row()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var r0 = await admin.GetAsync(Bundle);
        Assert.True(r0.Status == HttpStatusCode.OK, r0.ToString());
        var bundle = r0.Fields;
        var before = (await admin.GetAsync($"{WeirClient.Api}/processing/libraries")).Elements;

        var bad = bundle.DeepClone().AsObject();
        var movies = bad["libraries"]!.AsArray().First(row => (string?)row!["name"] == "Movies")!;
        movies["watched_folder"] = "/srv/movies/in";
        movies["output_folder"] = "";

        var put = await admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = bad });
        Assert.True(put.Status == HttpStatusCode.BadRequest, put.ToString());
        var detail = (string)put.Fields["detail"]!;
        Assert.Contains("Movies", detail, StringComparison.Ordinal);
        Assert.Contains("output folder", detail, StringComparison.Ordinal);

        var after = (await admin.GetAsync($"{WeirClient.Api}/processing/libraries")).Elements;
        Assert.Equal(
            before.Select(row => (string?)row!["watched_folder"]),
            after.Select(row => (string?)row!["watched_folder"]));
    }

    [Fact]
    public async Task Configuration_backup_list_shape()
    {
        using var admin = await fixture.Server.CreateAdminClientAsync();
        var response = await admin.GetAsync($"{WeirClient.Api}/suite/configuration-backups");
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        Assert.True(response.Fields.ContainsKey("directory"));
        Assert.IsType<JsonArray>(response.Fields["items"]);
    }
}
