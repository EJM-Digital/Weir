using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weir.Api.Endpoints;
using Weir.Core.Configuration;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Logging;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Sqlite;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// What System's overview, scheduled tasks and live log are built from, over HTTP: the two endpoints, the <c>system.tasks</c> and
/// <c>system.log</c> frames on the Activity stream, and the figures that need a real server behind them.
/// </summary>
public sealed class SystemOverviewApiTests
{
    private const string Overview = "/api/v1/system/overview";
    private const string Tasks = "/api/v1/system/tasks";
    private static readonly DateTimeOffset Noon = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(WeirTestServer Server, ApiTestClient Client)> StartAsync(Action<string>? prepareHome = null)
    {
        var server = await WeirTestServer.StartAsync(
            [("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0")], prepareHome: prepareHome);
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        return (server, client);
    }

    private static async Task<StreamReader> OpenStreamAsync(WeirTestServer server, ApiTestClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/activity/stream");
        request.Headers.Add("Cookie", string.Join("; ", client.Cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return new StreamReader(await response.Content.ReadAsStreamAsync());
    }

    /// <summary>The data of the next frame named <paramref name="name"/>, repeating <paramref name="provoke"/> until one arrives so the test waits out the stream's own start.</summary>
    /// <summary>The first <paramref name="name"/> frame that <paramref name="settled"/> accepts, provoking until one arrives.</summary>
    private static async Task<JsonNode> NextFrameAsync(StreamReader reader, string name, Action provoke, Func<JsonNode, bool>? settled = null)
    {
        var pending = ReadFrameAsync(reader, name, settled ?? (_ => true));
        for (var attempt = 0; attempt < 50 && !pending.IsCompleted; attempt++)
        {
            provoke();
            await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(200)));
        }

        return await pending.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<JsonNode> ReadFrameAsync(StreamReader reader, string name, Func<JsonNode, bool> settled)
    {
        string? eventName = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventName == name
                     && JsonNode.Parse(line["data: ".Length..]) is { } frame && settled(frame))
            {
                return frame;
            }
        }

        throw new InvalidOperationException("The stream ended.");
    }

    [Theory]
    [InlineData(Overview)]
    [InlineData(Tasks)]
    public async Task Both_endpoints_need_a_signed_in_user(string path)
    {
        await using var server = await WeirTestServer.StartAsync();
        using var anonymous = await server.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task The_overview_reads_as_the_documented_json()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;

        using var response = await client.GetAsync(Overview);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = (JsonObject)await Json(response);
        Assert.Equal(
            ["version", "update", "uptime_seconds", "started_at", "runs_as", "address", "data_bytes", "browsers_live", "requests", "jobs_today", "restarts_this_week", "checks", "last_update_backup"],
            json.Select(pair => pair.Key));
        Assert.Null(json["last_update_backup"]);
        Assert.Equal(["status", "latest_version"], ((JsonObject)json["update"]!).Select(pair => pair.Key));
        Assert.Equal(["median_ms", "p95_ms", "errors_today"], ((JsonObject)json["requests"]!).Select(pair => pair.Key));
        Assert.Equal(["run", "failed"], ((JsonObject)json["jobs_today"]!).Select(pair => pair.Key));
        Assert.Equal(["passing", "total"], ((JsonObject)json["checks"]!).Select(pair => pair.Key));
    }

    [Fact]
    public async Task The_overview_says_how_this_copy_runs_where_it_listens_and_how_long_it_has_been_up()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;

        var json = await Json(await client.GetAsync(Overview));

        Assert.Equal("app", json["runs_as"]!.GetValue<string>());
        Assert.Matches(@"^http://[^/:]+:\d+$", json["address"]!.GetValue<string>());
        Assert.EndsWith("Z", json["started_at"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.InRange(json["uptime_seconds"]!.GetValue<long>(), 0, 600);
        Assert.True(json["data_bytes"]!.GetValue<long>() > 0);
        Assert.Equal(0, json["restarts_this_week"]!.GetValue<long>());
    }

    [Fact]
    public async Task The_overview_counts_every_browser_holding_the_stream_open()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;
        Assert.Equal(0, (await Json(await client.GetAsync(Overview)))["browsers_live"]!.GetValue<int>());

        using var reader = await OpenStreamAsync(server, client);
        await Eventually.ThatAsync(async () => (await Json(await client.GetAsync(Overview)))["browsers_live"]!.GetValue<int>() == 1);
    }

    [Fact]
    public async Task The_overview_counts_the_checks_that_pass_out_of_those_it_ran()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;

        var checks = (await Json(await client.GetAsync(Overview)))["checks"]!;

        // The database, the workers, the folder watcher and the media tools, and no connections yet.
        Assert.Equal(4, checks["total"]!.GetValue<int>());
        Assert.InRange(checks["passing"]!.GetValue<int>(), 1, 4);
    }

    [Fact]
    public async Task Restarts_this_week_counts_the_starts_after_the_first()
    {
        var (server, client) = await StartAsync(prepareHome: SeedTwoEarlierStarts);
        await using var _server = server;

        var json = await Json(await client.GetAsync(Overview));

        // Three days ago was the first start of this install, yesterday was a restart, and this start is another.
        Assert.Equal(2, json["restarts_this_week"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_start_that_updates_the_database_saves_a_copy_first_and_the_overview_says_where()
    {
        var (server, client) = await StartAsync(prepareHome: SeedDatabaseAtBaseline);
        await using var _server = server;

        var json = await Json(await client.GetAsync(Overview));

        var backup = (JsonObject)json["last_update_backup"]!;
        Assert.Equal(["path", "taken_at", "from_version", "to_version", "in_data_folder"], backup.Select(pair => pair.Key));
        var path = backup["path"]!.GetValue<string>();
        Assert.Equal(Path.Join(server.Home, "backups", "pre-update"), Path.GetDirectoryName(path));
        Assert.Matches(@"^weir-0036-to-.+-\d{8}T\d{6}Z\.db$", Path.GetFileName(path));
        Assert.True(File.Exists(path));
        Assert.EndsWith("Z", backup["taken_at"]!.GetValue<string>(), StringComparison.Ordinal);
        // The server that starts after an update does not know what ran before it.
        Assert.Null(backup["from_version"]);
        Assert.False(string.IsNullOrEmpty(backup["to_version"]!.GetValue<string>()));
        Assert.True(backup["in_data_folder"]!.GetValue<bool>());
        // A start that went well leaves no reason for the tray, and its progress says it got as far as ready.
        Assert.Contains("\"phase\":\"ready\"", File.ReadAllText(Path.Join(server.Home, StartupProgress.FileName)), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(server.Home, StartupNotes.ErrorFileName)));
    }

    [Fact]
    public async Task A_running_server_saves_a_copy_when_the_tray_asks_and_the_overview_names_both_versions()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;
        var request = Path.Join(server.Home, TrayUpdateBackupWatcher.RequestFileName);
        var result = Path.Join(server.Home, TrayUpdateBackupWatcher.ResultFileName);

        File.WriteAllText(request, $"{{\"id\": \"abc\", \"requested_at\": \"{DateTimeOffset.UtcNow:O}\", \"target_version\": \"1.0.0-rc.99\"}}");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonNode? answer = null;
        while (DateTime.UtcNow < deadline && (string?)answer?["state"] is not ("saved" or "failed"))
        {
            await Task.Delay(50);
            answer = ReadResult(result);
        }

        Assert.Equal("saved", (string?)answer?["state"]);
        var json = await Json(await client.GetAsync(Overview));
        var backup = (JsonObject)json["last_update_backup"]!;
        Assert.Equal(json["version"]!.GetValue<string>(), backup["from_version"]!.GetValue<string>());
        Assert.Equal("1.0.0-rc.99", backup["to_version"]!.GetValue<string>());
        Assert.Equal((string?)answer?["path"], backup["path"]!.GetValue<string>());
    }

    private static JsonNode? ReadResult(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [Fact]
    public async Task A_start_that_cannot_save_the_copy_does_not_update_the_database()
    {
        var home = Path.Join(Path.GetTempPath(), "weir-api-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var error = await Assert.ThrowsAsync<PreUpdateBackupException>(() => WeirTestServer.StartAsync(
                home: home,
                prepareHome: path =>
                {
                    SeedDatabaseAtBaseline(path);
                    Directory.CreateDirectory(Path.Join(path, "backups"));
                    File.WriteAllText(Path.Join(path, "backups", "pre-update"), "in the way");
                }));

            Assert.StartsWith("Weir couldn't save a copy of its data before updating, so it didn't change anything: ", error.Message, StringComparison.Ordinal);

            // The tray shows the reason instead of a bare "couldn't start", and has no copy in progress to wait for.
            var lines = File.ReadAllText(Path.Join(home, StartupNotes.ErrorFileName)).Split('\n', 2);
            Assert.Equal(PreUpdateBackupException.Headline, lines[0]);
            Assert.Equal(error.Message, lines[1]);
            Assert.Contains("\"phase\":\"opening_database\"", File.ReadAllText(Path.Join(home, StartupProgress.FileName)), StringComparison.Ordinal);
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Join(home, "data", "weir.sqlite3")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version_num FROM alembic_version";
            Assert.Equal("0036_drop_pruner_tables", command.ExecuteScalar());
        }
        finally
        {
            new SqliteDatabase(Path.Join(home, "data", "weir.sqlite3")).ClearPool();
            Directory.Delete(home, recursive: true);
        }
    }

    private static void SeedDatabaseAtBaseline(string home)
    {
        var database = new SqliteDatabase(Path.Join(home, "data", "weir.sqlite3"));
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
        new SchemaMigrator(database).EnsureAtBaseline();
        database.ClearPool();
    }

    private static void SeedTwoEarlierStarts(string home)
    {
        var database = new SqliteDatabase(Path.Join(home, "data", "weir.sqlite3"));
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
        new SchemaMigrator(database).EnsureAtHead();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            var now = DateTime.UtcNow;
            command.CommandText = "INSERT INTO server_starts (started_at) VALUES (@first), (@second)";
            command.Parameters.AddWithValue("@first", now.AddDays(-3).ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@second", now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        database.ClearPool();
    }

    [Fact]
    public async Task The_tasks_list_has_every_labelled_task_once_it_has_run()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;

        await Eventually.ThatAsync(async () =>
            (await Json(await client.GetAsync(Tasks))).AsArray().Any(task => task!["key"]!.GetValue<string>() == "suite-log-retention" && task["last_ok"]?.GetValue<bool>() == true));

        var list = (await Json(await client.GetAsync(Tasks))).AsArray();
        var log = list.Single(task => task!["key"]!.GetValue<string>() == "suite-log-retention")!;
        Assert.Equal(["key", "label", "running", "last_run_at", "last_ok", "last_error", "next_run_at", "interval_seconds"], ((JsonObject)log).Select(pair => pair.Key));
        Assert.Equal(("Trim the log", false, 3600), (log["label"]!.GetValue<string>(), log["running"]!.GetValue<bool>(), log["interval_seconds"]!.GetValue<int>()));
        Assert.EndsWith("Z", log["next_run_at"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(list, task => task!["key"]!.GetValue<string>() is "activity-latest-poll" or "connection-usage-flush");
    }

    [Fact]
    public async Task A_task_starting_and_ending_reaches_an_open_stream_with_the_whole_list()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;
        var tasks = server.Services.GetRequiredService<PeriodicTaskRegistry>();
        tasks.Plan("test-task", "A test task", DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(5));
        using var reader = await OpenStreamAsync(server, client);

        var frame = await NextFrameAsync(
            reader,
            "system.tasks",
            () =>
            {
                tasks.Begin("test-task");
                tasks.End("test-task", ok: false, error: "The test said so.");
            },
            frame => frame.AsArray().Any(entry => entry!["key"]!.GetValue<string>() == "test-task" && entry["last_ok"] is not null));

        var task = frame.AsArray().Single(entry => entry!["key"]!.GetValue<string>() == "test-task")!;
        Assert.Equal(("A test task", false, false, "The test said so."), (
            task["label"]!.GetValue<string>(), task["running"]!.GetValue<bool>(), task["last_ok"]!.GetValue<bool>(), task["last_error"]!.GetValue<string>()));
        Assert.Contains(frame.AsArray(), entry => entry!["key"]!.GetValue<string>() == "suite-log-retention");
    }

    [Fact]
    public async Task A_warning_and_information_from_weirs_own_loggers_reach_an_open_stream_with_their_levels()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;
        var logger = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("weir.test");
        using var reader = await OpenStreamAsync(server, client);

        var warning = await NextFrameAsync(reader, "system.log", () => logger.LogWarning("The disk on {Drive} is nearly full.", "D:"), frame => frame["message"]!.GetValue<string>() == "The disk on D: is nearly full.");
        var information = await NextFrameAsync(reader, "system.log", () => logger.LogInformation("Scanned {Library}.", "Movies"), frame => frame["message"]!.GetValue<string>() == "Scanned Movies.");

        Assert.Equal("WARNING", warning["level"]!.GetValue<string>());
        Assert.Equal("INFO", information["level"]!.GetValue<string>());
        Assert.EndsWith("Z", information["at"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Information_from_a_logger_that_is_not_weirs_does_not_reach_an_open_stream()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;
        var factory = server.Services.GetRequiredService<ILoggerFactory>();
        var other = factory.CreateLogger("Some.Library.Chatter");
        var weir = factory.CreateLogger("weir.test");
        using var reader = await OpenStreamAsync(server, client);

        var frame = await NextFrameAsync(reader, "system.log", () =>
        {
            other.LogInformation("Nothing to see.");
            weir.LogError("The folder is gone.");
        });

        Assert.Equal(("ERROR", "The folder is gone."), (frame["level"]!.GetValue<string>(), frame["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_overview_reaches_a_stream_that_opens_and_again_when_a_fact_about_weir_changes()
    {
        var (server, client) = await StartAsync();
        await using var _server = server;
        using var first = await OpenStreamAsync(server, client);

        var opening = await NextFrameAsync(first, "system.overview", () => { });
        using var second = await OpenStreamAsync(server, client);
        var changed = await NextFrameAsync(first, "system.overview", () => { }, frame => frame["browsers_live"]!.GetValue<int>() == 2);

        var documented = (JsonObject)await Json(await client.GetAsync(Overview));
        Assert.Equal(documented.Select(pair => pair.Key), ((JsonObject)opening).Select(pair => pair.Key));
        Assert.Equal(1, opening["browsers_live"]!.GetValue<int>());
        Assert.Equal(2, changed["browsers_live"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_overview_frames_send_the_facts_when_a_stream_opens_and_only_again_when_one_changes()
    {
        var (server, _) = await StartAsync();
        await using var _server = server;
        var frames = server.Services.GetRequiredService<SystemOverviewFrames>();
        using var stop = new CancellationTokenSource();
        await using var stream = frames.ForAsync(stop.Token).GetAsyncEnumerator(stop.Token);

        Assert.True(await stream.MoveNextAsync());
        Assert.StartsWith("event: system.overview\ndata: {\"version\":", stream.Current, StringComparison.Ordinal);
        var next = stream.MoveNextAsync().AsTask();

        // A server that has only just started may still be settling (the update check answering), and says so: wait for looks
        // that find nothing new, which is the case being tested.
        var quietLooks = 0;
        for (var look = 0; look < 20 && quietLooks < 3; look++)
        {
            await frames.RefreshAsync(stop.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            if (next.IsCompleted)
            {
                quietLooks = 0;
                Assert.True(await next);
                next = stream.MoveNextAsync().AsTask();
            }
            else
            {
                quietLooks++;
            }
        }

        Assert.Equal(3, quietLooks);

        using var browser = server.Services.GetRequiredService<ActivityStreamClients>().Open();
        await frames.RefreshAsync(stop.Token);

        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("\"browsers_live\":1", stream.Current, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overview_frame_is_the_overview_as_the_endpoint_answers_it()
    {
        var overview = new Weir.Core.Json.WireObject().Set("version", "1.0.0").Set("uptime_seconds", 5L);

        Assert.Equal("event: system.overview\ndata: {\"version\":\"1.0.0\",\"uptime_seconds\":5}\n\n", SystemOverviewFrames.Frame(overview));
    }

    [Fact]
    public void The_tasks_frame_is_the_documented_json()
    {
        var frame = SystemTasksFrames.Frame(
        [
            new PeriodicTaskStatus("scan-1", "Scan Movies", true, Noon, true, null, Noon.AddMinutes(5), TimeSpan.FromMinutes(5)),
            new PeriodicTaskStatus("trim-log", "Trim the log", false, null, null, null, null, null),
        ]);

        Assert.Equal(
            "event: system.tasks\ndata: [" +
            "{\"key\":\"scan-1\",\"label\":\"Scan Movies\",\"running\":true,\"last_run_at\":\"2026-10-02T12:00:00Z\",\"last_ok\":true,\"last_error\":null," +
            "\"next_run_at\":\"2026-10-02T12:05:00Z\",\"interval_seconds\":300}," +
            "{\"key\":\"trim-log\",\"label\":\"Trim the log\",\"running\":false,\"last_run_at\":null,\"last_ok\":null,\"last_error\":null,\"next_run_at\":null,\"interval_seconds\":null}" +
            "]\n\n",
            frame);
    }

    [Fact]
    public void The_log_frame_is_the_documented_json()
    {
        var frame = SystemLogFrames.Frame(new LogAlert(Noon, "ERROR", "Weir could not read a folder."));

        Assert.Equal(
            "event: system.log\ndata: {\"at\":\"2026-10-02T12:00:00Z\",\"level\":\"ERROR\",\"message\":\"Weir could not read a folder.\"}\n\n",
            frame);
    }

    [Theory]
    [InlineData("0.0.0.0", 9347, "http://RIG:9347")]
    [InlineData("*", 8080, "http://RIG:8080")]
    [InlineData("localhost", 9347, "http://localhost:9347")]
    [InlineData("127.0.0.1", 18788, "http://localhost:18788")]
    [InlineData("192.168.1.20", 9347, "http://192.168.1.20:9347")]
    [InlineData("::", 9347, "http://RIG:9347")]
    [InlineData("fd00::20", 9347, "http://[fd00::20]:9347")]
    public void The_address_is_where_a_browser_reaches_this_weir(string host, int port, string expected)
    {
        var machine = MachineIdentity.From("RIG");

        Assert.Equal(expected, SystemOverviewReader.AddressOf(new ServerListenOptions(host, port), machine));
    }
}
