using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Weir.ArrRecorder;

/// <summary>The app's own API, used to put it in the state a session needs. None of these calls is recorded.</summary>
internal sealed partial class ArrScenario
{
    private int _downloadClientId = 1;

    private async Task SetUpAsync(WebhookReceiver receiver)
    {
        await _api.PostAsync("/api/v3/rootfolder", new JsonObject { ["path"] = _flavour.RootFolder }).ConfigureAwait(false);
        var profiles = (await _api.GetAsync("/api/v3/qualityprofile").ConfigureAwait(false))!.AsArray();
        _qualityProfileId = profiles[0]!["id"]!.GetValue<long>();
        Say($"root folder {_flavour.RootFolder}, quality profile {_qualityProfileId} ({(string)profiles[0]!["name"]!})");

        await AddWebhookAsync().ConfigureAwait(false);
        Expect(await receiver.WaitForAsync("Test", TimeSpan.FromSeconds(60)).ConfigureAwait(false), "the app never posted its Test webhook");
        await AddDownloadClientAsync().ConfigureAwait(false);
        await AddTitleAsync().ConfigureAwait(false);
    }

    private async Task AddWebhookAsync()
    {
        var schema = (await _api.GetAsync("/api/v3/notification/schema").ConfigureAwait(false))!.AsArray();
        var webhook = schema.First(item => (string?)item!["implementation"] == "Webhook")!.AsObject().DeepClone().AsObject();
        webhook["name"] = "Weir";
        foreach (var flag in new[] { "onGrab", "onDownload", "onUpgrade", "onRename", "onSeriesAdd", "onSeriesDelete", "onEpisodeFileDelete", "onMovieAdded", "onMovieDelete", "onMovieFileDelete" })
        {
            if (webhook.ContainsKey(flag))
            {
                webhook[flag] = true;
            }
        }

        SetField(webhook, "url", $"http://{_settings.WebhookHost}:{_settings.WebhookPort}/api/v1/intake/webhook/{_flavour.Kind}");
        SetField(webhook, "method", 1);
        SetField(webhook, "headers", new JsonArray(new JsonObject { ["key"] = "X-Webhook-Secret", ["value"] = WebhookSecret }));

        // Saving a notification makes the app send its Test webhook first.
        await _api.PostAsync("/api/v3/notification", webhook).ConfigureAwait(false);
    }

    private async Task AddDownloadClientAsync()
    {
        await Wait.UntilAsync("the download client to answer", TimeSpan.FromMinutes(3), async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(_settings.TransmissionUrl + "/transmission/rpc").ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        var schema = (await _api.GetAsync("/api/v3/downloadclient/schema").ConfigureAwait(false))!.AsArray();
        var client = schema.First(item => (string?)item!["implementation"] == "Transmission")!.AsObject().DeepClone().AsObject();
        client["name"] = "Transmission";
        client["enable"] = true;
        SetField(client, "host", "transmission");
        SetField(client, "port", 9091);
        var saved = await _api.PostAsync("/api/v3/downloadclient?forceSave=true", client).ConfigureAwait(false);
        _downloadClientId = saved!["id"]!.GetValue<int>();
        try
        {
            await _api.PostAsync("/api/v3/downloadclient/test", saved.AsObject().DeepClone()).ConfigureAwait(false);
            Say("the download client passed the app's own test");
        }
        catch (RecorderException exception)
        {
            Say($"the download client failed the app's own test: {exception.Message}");
        }
    }

    private async Task AddTitleAsync()
    {
        var found = (await _api.GetAsync(_flavour.LookupPath).ConfigureAwait(false))!.AsArray();
        var title = found[0]!.AsObject().DeepClone().AsObject();
        title["qualityProfileId"] = _qualityProfileId;
        title["rootFolderPath"] = _flavour.RootFolder;
        title["monitored"] = true;
        if (_flavour.IsSonarr)
        {
            title["seasonFolder"] = true;
            title["addOptions"] = new JsonObject { ["monitor"] = "all", ["searchForMissingEpisodes"] = false, ["searchForCutoffUnmetEpisodes"] = false };
        }
        else
        {
            title["minimumAvailability"] = "announced";
            title["addOptions"] = new JsonObject { ["monitor"] = "movieOnly", ["searchForMovie"] = false };
        }

        var added = await _api.PostAsync(_flavour.TitlePath, title).ConfigureAwait(false);
        _titleId = added!["id"]!.GetValue<long>();
        Say($"added {(string)added["title"]!} as {_titleId}");
        if (_flavour.IsSonarr)
        {
            await Wait.UntilAsync("the series' episodes", TimeSpan.FromMinutes(4), async () => (await _api.GetAsync($"/api/v3/episode?seriesId={_titleId}").ConfigureAwait(false))!.AsArray().Count > 1).ConfigureAwait(false);
        }
        else
        {
            await Wait.UntilAsync("the app to finish refreshing", TimeSpan.FromMinutes(4), AllCommandsDoneAsync).ConfigureAwait(false);
        }
    }

    private async Task<bool> AllCommandsDoneAsync() =>
        (await _api.GetAsync("/api/v3/command").ConfigureAwait(false))!.AsArray().All(command => (string?)command!["status"] is "completed" or "failed" or "aborted" or "cancelled");

    /// <summary>Hands the app a release for the title, which it passes to its download client.</summary>
    private Task PushReleaseAsync() => TryAsync("push a release", async () =>
    {
        var body = new JsonObject
        {
            ["title"] = _flavour.ReleaseTitle,
            ["downloadUrl"] = $"http://{_settings.WebhookHost}:{_settings.WebhookPort}/torrents/{_flavour.ReleaseTitle}.torrent",
            ["protocol"] = "torrent",
            ["publishDate"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        var answer = await _api.PostAsync("/api/v3/release/push", body).ConfigureAwait(false);
        var release = answer is JsonArray list ? list[0] : answer;
        var shown = release?.ToJsonString() ?? "nothing";
        Say($"release push: {shown[..Math.Min(400, shown.Length)]}");
        Expect(release?["approved"]?.GetValue<bool>() == true || release?["rejected"]?.GetValue<bool>() == false, "the app did not take the release");
        await Wait.UntilAsync("the release in the app's queue", TimeSpan.FromMinutes(4), async () =>
        {
            await _api.PostAsync("/api/v3/command", new JsonObject { ["name"] = "RefreshMonitoredDownloads" }).ConfigureAwait(false);
            return ((await _api.GetAsync("/api/v3/queue?pageSize=50").ConfigureAwait(false))?["totalRecords"]?.GetValue<int>() ?? 0) > 0;
        }).ConfigureAwait(false);
    });

    private void MakeDownloadedFile()
    {
        var folder = Path.Combine(_settings.WorkFolder, _flavour.ImportName);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, _flavour.ImportName + ".mkv");

        // Longer than two minutes, so neither app takes it for a sample.
        var start = new ProcessStartInfo("ffmpeg")
        {
            ArgumentList =
            {
                "-y", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=duration=130:size=160x120:rate=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=130",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", file,
            },
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new RecorderException("ffmpeg did not start");
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Fail("make the downloaded file", new RecorderException(errors));
        }
    }

    private Task WaitForImportAsync() => TryAsync("wait for the import", async () =>
    {
        try
        {
            await Wait.UntilAsync("the app to import the file", TimeSpan.FromMinutes(4), async () => _flavour.IsSonarr
                ? (await _api.GetAsync($"/api/v3/episodefile?seriesId={_titleId}").ConfigureAwait(false))!.AsArray().Count > 0
                : (await _api.GetAsync($"/api/v3/movie/{_titleId}").ConfigureAwait(false))!["hasFile"]!.GetValue<bool>()).ConfigureAwait(false);
        }
        catch (RecorderException)
        {
            foreach (var command in (await _api.GetAsync("/api/v3/command").ConfigureAwait(false))!.AsArray())
            {
                Say($"  command {(string?)command!["name"]}: {(string?)command["status"]} {(string?)command["message"]} {(string?)command["exception"]}");
            }

            throw;
        }
    });

    private Task AddRemotePathMappingAsync() => TryAsync("add a remote path mapping", async () =>
    {
        await _api.PostAsync("/api/v3/remotepathmapping", new JsonObject { ["host"] = "transmission", ["remotePath"] = "/remote-downloads/", ["localPath"] = "/downloads/" }).ConfigureAwait(false);
    });

    private async Task TryAsync(string what, Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fail(what, exception);
        }
    }

    private static void SetField(JsonObject resource, string name, JsonNode value)
    {
        var field = resource["fields"]!.AsArray().FirstOrDefault(item => (string?)item!["name"] == name)
            ?? throw new RecorderException($"{(string?)resource["implementation"]} has no setting called {name}");
        field["value"] = value;
    }
}
