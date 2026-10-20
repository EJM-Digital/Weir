using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.MediaManagers;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>
/// An install a person has set up: rule sets, workflows with schedules, operator and suite settings, direct play devices, two alerts, a
/// media manager and a download client with their secrets. Every step goes through the public API. <see cref="ExportAsync"/> is the
/// backup the Settings screen downloads; <see cref="RestoreAsync(System.Text.Json.Nodes.JsonNode)"/> is the restore it uploads.
/// </summary>
internal sealed class BackupWorld : IAsyncDisposable
{
    public const string Api = WeirClient.Api;
    public const string Bundle = $"{Api}/suite/configuration-bundle";
    public const string Channels = $"{Api}/suite/notification-channels";
    public const string BackupPassword = $"{Api}/suite/backup-password";
    public const string Backups = $"{Api}/suite/configuration-backups";
    public const string Secret = "download-client-secret-31ac";

    private readonly TemporaryFolder _folders = new();
    private readonly List<IDisposable> _fakes = [];

    private BackupWorld(WeirServer server, WeirClient admin)
    {
        Server = server;
        Admin = admin;
    }

    public WeirServer Server { get; }

    public WeirClient Admin { get; }

    public FakeManager Manager { get; private set; } = null!;

    public FakeDownloadClient DownloadClient { get; private set; } = null!;

    public JsonObject DownloadClientConnection { get; private set; } = null!;

    public JsonObject ManagerConnection { get; private set; } = null!;

    public static async Task<BackupWorld> StartAsync()
    {
        // A machine whose clock is not UTC: the times a backup carries must not move with it.
        var server = await WeirServer.StartNewAsync(new Dictionary<string, string> { ["TZ"] = "Pacific/Auckland" });
        return new BackupWorld(server, await server.CreateAdminClientAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await Server.DisposeAsync();
        foreach (var fake in _fakes)
        {
            fake.Dispose();
        }

        _folders.Dispose();
    }

    public string Folder(params string[] parts)
    {
        var path = Path.Combine([_folders.Path, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    // --- building the install --------------------------------------------------------------------------

    /// <summary>Sets everything the backup holds to something other than its default, so a restore that forgets a section shows.</summary>
    public async Task SetUpAsync()
    {
        await SaveSuiteSettingsAsync(timezone: "Pacific/Auckland", logRetention: 45, activityRetention: 120, backupHours: 12, backupTime: "03:30");
        await SaveOperatorSettingsAsync(first: true);
        var rules = await SaveRuleSetsAsync(first: true);
        await SaveLibrariesAsync(rules, first: true);
        await SaveDevicesAsync("roku", "apple_tv_4k");
        await CreateAlertAsync("Webhook alert", "webhook", "https://hooks.example.com/one", ["job_failed", "processing_job_completed"], enabled: true);
        await CreateAlertAsync("Discord alert", "discord", "https://discord.example.com/api/webhooks/two", ["processing_job_failed"], enabled: false);
        await CreateConnectionsAsync();
    }

    /// <summary>Changes everything the backup holds, and removes and adds what a person might, so a restore has plenty to put back.</summary>
    public async Task ChangeEverythingAsync()
    {
        await SaveSuiteSettingsAsync(timezone: "Europe/Paris", logRetention: 7, activityRetention: 0, backupHours: 99, backupTime: "22:15");
        await SaveOperatorSettingsAsync(first: false);
        var rules = await SaveRuleSetsAsync(first: false);
        await SaveLibrariesAsync(rules, first: false);
        await SaveDevicesAsync("lg_webos");

        var channels = (await Admin.GetAsync(Channels)).Fields["items"]!.AsArray();
        var discord = channels.Single(item => (string?)item!["label"] == "Discord alert")!;
        var deleted = await Admin.DeleteWithCsrfAsync($"{Channels}/{(long)discord["id"]!}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
        var webhook = channels.Single(item => (string?)item!["label"] == "Webhook alert")!;
        var edited = await Admin.PutWithCsrfAsync(
            $"{Channels}/{(long)webhook["id"]!}",
            new JsonObject
            {
                ["label"] = "Webhook alert",
                ["provider"] = "webhook",
                ["url"] = "https://hooks.example.com/changed",
                ["events"] = new JsonArray("job_completed"),
                ["enabled"] = false,
            });
        Assert.True(edited.Status == HttpStatusCode.OK, edited.ToString());
    }

    private async Task SaveSuiteSettingsAsync(string timezone, int logRetention, int activityRetention, int backupHours, string backupTime)
    {
        var saved = await Admin.PutWithCsrfAsync(
            $"{Api}/suite/settings",
            new JsonObject
            {
                ["app_timezone"] = timezone,
                ["log_retention_days"] = logRetention,
                ["activity_retention_days"] = activityRetention,
                ["configuration_backup_enabled"] = activityRetention != 0,
                ["configuration_backup_interval_hours"] = Math.Min(backupHours, 720),
                ["configuration_backup_preferred_time"] = backupTime,
            });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    /// <summary>Every operator setting the server documents, saved at a value other than the one it has (the schema gives the kind and range).</summary>
    private async Task SaveOperatorSettingsAsync(bool first)
    {
        var current = (await Admin.GetAsync($"{Api}/processing/operator-settings")).Fields;
        using var anonymous = Server.CreateClient();
        var document = await anonymous.GetAsync("/openapi.json");
        var settings = document.Fields["components"]!["schemas"]!["ProcessingOperatorSettingsPutIn"]!["properties"]!.AsObject();
        var body = new JsonObject();
        foreach (var (name, schema) in settings.Where(setting => setting.Key != "csrf_token"))
        {
            var value = schema!["anyOf"]?.AsArray().Select(alternative => alternative!.AsObject()).Single(alternative => (string?)alternative["type"] != "null") ?? schema.AsObject();
            if ((string?)value["type"] == "boolean")
            {
                body[name] = first ? !(bool)current[name]! : (bool)current[name]!;
            }
            else
            {
                var low = (long)(double)value["minimum"]!;
                var high = (long)(double)value["maximum"]!;
                body[name] = first ? (current[name]?.GetValue<long>() == high ? low : high) : (low + high) / 2;
            }
        }

        var saved = await Admin.PutWithCsrfAsync($"{Api}/processing/operator-settings", body);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    private async Task<long[]> SaveRuleSetsAsync(bool first)
    {
        var accepted = await RuleSetSettingsAsync();
        var ids = new List<long>();
        foreach (var (name, other) in new[] { ("Rules one", false), ("Rules two", true) })
        {
            var body = new JsonObject { ["name"] = first ? name : name + " changed" };
            foreach (var (setting, schema) in accepted)
            {
                body[setting] = Value(setting, schema, first ^ other);
            }

            var existing = (await Admin.GetAsync($"{Api}/processing/rule-sets")).Elements.FirstOrDefault(item => (string?)item!["name"] == name);
            var saved = existing is null
                ? await Admin.PostWithCsrfAsync($"{Api}/processing/rule-sets", body)
                : await Admin.PutWithCsrfAsync($"{Api}/processing/rule-sets/{(long)existing["id"]!}", body);
            Assert.True(saved.Status is HttpStatusCode.Created or HttpStatusCode.OK, saved.ToString());
            ids.Add((long)saved.Fields["id"]!);
        }

        return [.. ids];
    }

    private async Task<Dictionary<string, JsonObject>> RuleSetSettingsAsync()
    {
        using var anonymous = Server.CreateClient();
        var document = await anonymous.GetAsync("/openapi.json");
        var properties = document.Fields["components"]!["schemas"]!["ProcessingRuleSetIn"]!["properties"]!.AsObject();
        return properties.Where(property => property.Key is not ("csrf_token" or "name")).ToDictionary(property => property.Key, property => property.Value!.AsObject());
    }

    /// <summary>A value for a rule set setting that is not its default; <paramref name="alternate"/> picks the second of two such values.</summary>
    private static JsonNode Value(string setting, JsonObject schema, bool alternate)
    {
        switch (setting)
        {
            case "primary_audio_lang": return alternate ? "jpn" : "eng";
            case "secondary_audio_lang": return alternate ? "fre" : "ger";
            case "tertiary_audio_lang": return alternate ? "spa" : "ita";
            case "subtitle_langs_csv": return alternate ? "eng" : "eng,fre";
            case "audio_sorters_json": return alternate ? "[{\"field\": \"bitrate\"}]" : "[{\"field\": \"channels\", \"value\": \">=5.1\"}]";
            case "subtitle_sorters_json": return alternate ? "[{\"field\": \"default\"}]" : "[{\"field\": \"language\", \"value\": \"fre\"}]";
            case "original_language_additional_csv": return alternate ? "kor" : "jpn";
            case "track_name_template": return alternate ? "{language}" : "{language} {codec}";
            case "subtitle_max_per_language": return alternate ? 4 : 2;
            case "track_name_overrides":
                return new JsonObject { ["forced"] = alternate ? "{language} F" : "{language} (Forced)", ["hearing_impaired"] = "{language} SDH", ["commentary"] = "{language} C", ["audio_description"] = "{language} AD" };
        }

        if ((string?)schema["type"] == "boolean")
        {
            return alternate ? (bool)schema["default"]! : !(bool)schema["default"]!;
        }

        var others = schema["enum"]!.AsArray().Select(choice => (string)choice!).Where(choice => choice != (string)schema["default"]!).ToList();
        return others[alternate && others.Count > 1 ? 1 : 0];
    }

    /// <summary>The two seeded workflows, each given folders, its own rule set, and a schedule of its own.</summary>
    private async Task SaveLibrariesAsync(long[] rules, bool first)
    {
        var libraries = (await Admin.GetAsync($"{Api}/processing/libraries")).Elements.Select(item => item!.AsObject()).OrderBy(item => (long)item["id"]!).ToList();
        for (var i = 0; i < libraries.Count; i++)
        {
            var body = LibraryBodies.Unchanged(libraries[i]);
            body["watched_folder"] = Folder(first ? "set-up" : "changed", $"watched-{i}");
            body["work_folder"] = Folder(first ? "set-up" : "changed", $"work-{i}");
            body["output_folder"] = Folder(first ? "set-up" : "changed", $"output-{i}");
            body["rule_set_id"] = rules[i];
            body["schedule_enabled"] = first;
            body["schedule_days"] = first ? "mon,wed,fri" : "sat";
            body["schedule_start"] = first ? "01:15" : "06:00";
            body["schedule_end"] = first ? "05:45" : "07:00";
            body["max_attempts"] = first ? 7 : 2;
            body["min_file_size_mb"] = first ? 120 : 10;
            body["rejected_file_action"] = first ? "delete_file" : "leave";
            var saved = await Admin.PutWithCsrfAsync($"{Api}/processing/libraries/{(long)libraries[i]["id"]!}", body);
            Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
        }
    }

    private async Task SaveDevicesAsync(params string[] selected)
    {
        var saved = await Admin.PutWithCsrfAsync(
            $"{Api}/processing/direct-play/devices", new JsonObject { ["selected"] = new JsonArray([.. selected.Select(id => (JsonNode?)JsonValue.Create(id))]) });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    private async Task CreateAlertAsync(string label, string provider, string url, string[] events, bool enabled)
    {
        var created = await Admin.PostWithCsrfAsync(
            Channels,
            new JsonObject
            {
                ["label"] = label,
                ["provider"] = provider,
                ["url"] = url,
                ["events"] = new JsonArray([.. events.Select(name => (JsonNode?)JsonValue.Create(name))]),
                ["enabled"] = enabled,
            });
        Assert.True(created.Status == HttpStatusCode.Created, created.ToString());
    }

    private async Task CreateConnectionsAsync()
    {
        Manager = FakeManager.StartDeluno();
        _fakes.Add(Manager);
        var manager = await Admin.PostWithCsrfAsync(
            ManagerConnections.Route,
            new JsonObject { ["kind"] = "deluno", ["name"] = "Deluno", ["base_url"] = Manager.BaseUrl, ["api_key"] = Manager.ApiKey, ["enabled"] = true });
        Assert.True(manager.Status is HttpStatusCode.OK or HttpStatusCode.Created, manager.ToString());
        ManagerConnection = manager.Fields;

        var kind = DownloadClients.Kind("sabnzbd");
        DownloadClient = DownloadClients.StartFake(kind, Secret);
        _fakes.Add(DownloadClient);
        DownloadClientConnection = await DownloadClients.CreateAsync(Admin, kind, DownloadClient, Secret);
    }

    // --- the backup ------------------------------------------------------------------------------------

    public async Task<JsonObject> ExportAsync()
    {
        var exported = await Admin.GetAsync(Bundle);
        Assert.True(exported.Status == HttpStatusCode.OK, exported.ToString());
        return exported.Fields;
    }

    public Task<WeirResponse> RestoreAsync(JsonNode bundle) =>
        Admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = bundle.DeepClone() });

    /// <summary>A restore that types the backup password a backup with locked secrets asks for.</summary>
    public Task<WeirResponse> RestoreAsync(JsonNode bundle, string password) =>
        Admin.PutWithCsrfAsync(Bundle, new JsonObject { ["bundle"] = bundle.DeepClone(), ["password"] = password });

    /// <summary>Sets the backup password, typed twice as the Backups screen asks.</summary>
    public async Task SetBackupPasswordAsync(string password)
    {
        var set = await Admin.PutWithCsrfAsync(BackupPassword, new JsonObject { ["password"] = password, ["confirm_password"] = password });
        Assert.True(set.Status == HttpStatusCode.OK, set.ToString());
    }

    /// <summary>"Back up now", then the stored file as the Backups screen downloads it: its text, as written on disk.</summary>
    public async Task<(JsonObject Item, string Text)> BackUpNowAsync()
    {
        var made = await Admin.PostWithCsrfAsync(Backups);
        Assert.True(made.Status == HttpStatusCode.Created, made.ToString());
        var download = await Admin.GetAsync($"{Backups}/{(long)made.Fields["id"]!}/download");
        Assert.True(download.Status == HttpStatusCode.OK, download.ToString());
        return (made.Fields, download.Text);
    }

    /// <summary>The alerts as the Settings screen lists them, by label.</summary>
    public async Task<Dictionary<string, JsonObject>> AlertsAsync() =>
        (await Admin.GetAsync(Channels)).Fields["items"]!.AsArray().ToDictionary(item => (string)item!["label"]!, item => item!.AsObject());

    /// <summary>Whether the media manager and the download client still sign in with the secrets saved before.</summary>
    public async Task<(bool Manager, bool DownloadClient)> SecretsStillWorkAsync()
    {
        var manager = await Admin.PostWithCsrfAsync($"{ManagerConnections.Route}/{(long)ManagerConnection["id"]!}/test");
        Assert.True(manager.Status == HttpStatusCode.OK, manager.ToString());
        var client = await DownloadClients.TestAsync(Admin, DownloadClientConnection);
        return ((bool)manager.Fields["ok"]!, (bool)client["ok"]!);
    }
}
