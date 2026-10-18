using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// The short scripted session that makes a release fixture (<c>scripts/make-release-fixtures.mjs</c>): it is run against a published
/// build, through its API like any other client, and leaves behind that build's own database and its own configuration backup.
/// It does nothing, and is skipped, unless the script names where to put them.
/// </summary>
[ContractArea("processing")]
public sealed class ReleaseFixtureSession
{
    /// <summary>Where to leave the database of the build under test; set by the script.</summary>
    public const string DatabaseOut = "WEIR_FIXTURE_DATABASE_OUT";

    /// <summary>Where to leave the configuration backup the build under test makes; set by the script.</summary>
    public const string BackupOut = "WEIR_FIXTURE_BACKUP_OUT";

    private const string Api = WeirClient.Api;

    [FixtureSessionFact]
    public async Task A_short_session_leaves_the_database_and_the_backup_of_the_build_under_test()
    {
        var databaseOut = Environment.GetEnvironmentVariable(DatabaseOut)!;
        var backupOut = Environment.GetEnvironmentVariable(BackupOut)!;
        await using var scenario = await Scenario.StartWithRealToolsAsync();

        // Alerts, a rule set, direct play devices: settings a person makes.
        await CreateAlertAsync(scenario.Admin);
        var rules = await scenario.Admin.PostWithCsrfAsync(
            $"{Api}/processing/rule-sets",
            new JsonObject
            {
                ["name"] = "Fixture rules",
                ["primary_audio_lang"] = "eng",
                ["secondary_audio_lang"] = "fre",
                ["subtitle_mode"] = "keep_listed",
                ["subtitle_langs_csv"] = "eng",
                ["remove_title"] = true,
            });
        Assert.True(rules.Status == HttpStatusCode.Created, rules.ToString());
        var devices = await scenario.Admin.PutWithCsrfAsync($"{Api}/processing/direct-play/devices", new JsonObject { ["selected"] = new JsonArray("roku") });
        Assert.True(devices.Status == HttpStatusCode.OK, devices.ToString());

        // A workflow a media manager feeds: one file finished, one refused for another release.
        var (_, linked) = await scenario.DelunoSetupAsync(
            capabilities: ["processor-reject-regrab"],
            library: [("name", "Fixture Movies"), ("rule_set_id", (long)rules.Fields["id"]!), ("failure_policy", "reject")]);
        var finished = Path.Combine(scenario.Folders.Watched, "Fixture.Film.2020", "Fixture.Film.2020.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(finished)!);
        await RealMedia.GenerateAsync(finished, seconds: 2, ["eng", "fre", "ger"], ["eng", "fre"]);
        await scenario.PostHandoffAsync("fixture-finished", finished, "Fixture.Film.2020");
        await scenario.WaitForHandoffStateAsync("fixture-finished", "completed");
        var imported = await scenario.PostOutcomeAsync("fixture-finished", "imported", importedPath: "/media/movies/Fixture Film (2020)/Fixture.Film.2020.mkv");
        Assert.True(imported.Status == HttpStatusCode.OK, imported.ToString());

        var silent = Path.Combine(scenario.Folders.Watched, "Fixture.Silent.2021", "Fixture.Silent.2021.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(silent)!);
        await RealMedia.GenerateAsync(silent, seconds: 2, []);
        await scenario.PostHandoffAsync("fixture-refused", silent, "Fixture.Silent.2021");
        await scenario.WaitForHandoffStateAsync("fixture-refused", "rejected");

        // A workflow of its own over a library folder, which Weir scans.
        var libraryRoot = Path.Combine(scenario.Root, "library");
        var libraryFolder = Directory.CreateDirectory(Path.Combine(libraryRoot, "Films")).FullName;
        var own = await scenario.CreateLibraryAsync(
            ("name", "Fixture Library"),
            ("watched_folder", Directory.CreateDirectory(Path.Combine(libraryRoot, "in")).FullName),
            ("work_folder", Directory.CreateDirectory(Path.Combine(libraryRoot, "work")).FullName),
            ("output_folder", Directory.CreateDirectory(Path.Combine(libraryRoot, "out")).FullName),
            ("rule_set_id", (long)rules.Fields["id"]!));
        var settings = await scenario.Admin.PutWithCsrfAsync(
            $"{Api}/processing/libraries/{(long)own["id"]!}/library-settings",
            new JsonObject { ["library_folders"] = new JsonArray(libraryFolder) });
        Assert.True(settings.Status == HttpStatusCode.OK, settings.ToString());
        await RealMedia.GenerateAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(libraryFolder, "Library Film (2019)")).FullName, "Library Film (2019).mkv"), seconds: 2, ["eng", "ger"], ["eng"]);
        await RealMedia.GenerateAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(libraryFolder, "Tidy Film (2018)")).FullName, "Tidy Film (2018).mkv"), seconds: 2, ["eng"], []);
        var scan = await scenario.Admin.PostWithCsrfAsync($"{Api}/processing/libraries/{(long)own["id"]!}/library-scan");
        Assert.True(scan.Status == HttpStatusCode.OK, scan.ToString());
        var jobId = (long)scan.Fields["job_id"]!;
        await new JobWait().UntilAsync(
            async () => [.. (await scenario.JobsAsync()).Where(job => (long)job["id"]! == jobId).Select(job => (string)job["status"]!)],
            async () => (await scenario.Admin.GetAsync($"{Api}/processing/libraries/{(long)own["id"]!}/library-files")).Fields["scan"]?["status"]?.GetValue<string>() == "completed" ? "done" : null,
            "the library scan");

        // The backup this build makes of the settings, then its database as it stands.
        var bundle = await scenario.Admin.GetAsync($"{Api}/suite/configuration-bundle");
        Assert.True(bundle.Status == HttpStatusCode.OK, bundle.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupOut))!);
        await File.WriteAllTextAsync(backupOut, bundle.Text);

        await using var stopped = await scenario.Server.StopForDatabaseAsync(restart: false);
        SeedSql.Execute(stopped.Connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databaseOut))!);
        File.Copy(scenario.Server.DatabasePath, databaseOut, overwrite: true);
        Assert.NotNull(linked);
    }

    private static async Task CreateAlertAsync(WeirClient admin)
    {
        var created = await admin.PostWithCsrfAsync(
            $"{Api}/suite/notification-channels",
            new JsonObject
            {
                ["label"] = "Fixture alert",
                ["provider"] = "webhook",
                ["url"] = "https://alerts.example.com/fixture",
                ["events"] = new JsonArray("job_failed", "processing_job_completed"),
            });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
    }
}

/// <summary>A test that runs only when the fixture script has named where to leave its files.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FixtureSessionFactAttribute : FactAttribute
{
    public FixtureSessionFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ReleaseFixtureSession.DatabaseOut)) || !RealFfmpeg.IsAvailable)
        {
            Skip = $"Runs only for scripts/make-release-fixtures.mjs, which sets {ReleaseFixtureSession.DatabaseOut} and has ffmpeg on PATH.";
        }
    }
}
