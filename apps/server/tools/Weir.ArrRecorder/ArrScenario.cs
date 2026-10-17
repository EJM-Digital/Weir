using System.Text.RegularExpressions;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ArrRecorder;

/// <summary>
/// One recorded session with a real Sonarr or Radarr. It sets the app up through the app's own API (a root folder, a title, a
/// download client, a webhook), then drives Weir's own client code against it, so a recording holds exactly what Weir sent and what the
/// app answered. Steps that fail are reported and the session carries on, so one drifted reply does not hide the others.
/// </summary>
internal sealed partial class ArrScenario : IDisposable
{
    private const string WebhookSecret = "recorder-webhook-secret";

    private readonly RecorderSettings _settings;
    private readonly ArrFlavour _flavour;
    private readonly ExchangeLog _log = new();
    private readonly List<string> _failures = [];

    private ArrApi? _apiOrNull;
    private RecordingWriter _writer = null!;
    private RecordingTransport _transport = null!;
    private IMediaManagerPort _port = null!;
    private ManagerConnection _connection = null!;
    private string _apiKey = string.Empty;
    private long _titleId;
    private long _qualityProfileId;
    private string? _downloadId;
    private string _filePath = string.Empty;

    public ArrScenario(RecorderSettings settings)
    {
        _settings = settings;
        _flavour = settings.Flavour;
    }

    private ArrApi _api => _apiOrNull ?? throw new InvalidOperationException("The session has not started.");

    public void Dispose() => _apiOrNull?.Dispose();

    public async Task<int> RunAsync()
    {
        using var receiver = WebhookReceiver.Start(_settings.WebhookPort);
        _apiKey = await ReadApiKeyAsync().ConfigureAwait(false);
        _apiOrNull = new ArrApi(_settings.Url, _apiKey);
        await Wait.UntilAsync($"{_flavour.Kind} to answer", TimeSpan.FromMinutes(4), async () => (await _api.GetAsync("/api/v3/system/status").ConfigureAwait(false)) is not null).ConfigureAwait(false);
        var version = (string)(await _api.GetAsync("/api/v3/system/status").ConfigureAwait(false))!["version"]!;
        Say($"{_flavour.Kind} {version} is up");

        _writer = new RecordingWriter(_flavour.Kind, version, _settings.OutputFolder, [_apiKey, WebhookSecret]);
        _transport = new RecordingTransport(new SocketsManagerHttpHandlerFactory(), _log);
        _port = new HttpMediaManagerPorts(_transport).PortForKind(_flavour.Kind)!;
        _connection = new ManagerConnection(_flavour.Kind, "recorder", _settings.Url, _apiKey);

        await ConnectionSteps().ConfigureAwait(false);
        await SetUpAsync(receiver).ConfigureAwait(false);
        await StepAsync("describe", "Asking an app with one root folder which folders it manages.", async () =>
        {
            var description = await _port.DescribeAsync(_connection).ConfigureAwait(false);
            Expect(description.Status == SignalStatus.Reported && description.LibraryRoots.Count == 1, "the app should report its one root folder");
        }).ConfigureAwait(false);
        await QueueSteps(receiver).ConfigureAwait(false);
        await ImportSteps(receiver).ConfigureAwait(false);
        await LibrarySteps().ConfigureAwait(false);
        await RedownloadSteps(receiver).ConfigureAwait(false);
        WriteWebhooks(receiver);

        Say(_failures.Count == 0 ? "Every step recorded." : $"{_failures.Count} step(s) failed:\n  " + string.Join("\n  ", _failures));
        return _failures.Count == 0 ? 0 : 1;
    }

    private async Task ConnectionSteps()
    {
        await StepAsync("connection-test-wrong-key", "The connection test (and the heartbeat) with an API key the app does not know.", async () =>
        {
            var wrong = _connection with { ApiKey = "00000000000000000000000000000000" };
            var (ok, detail) = await ManagerHealthProbe.ProbeAsync(_transport, wrong.Name, wrong.Kind, wrong.BaseUrl, wrong.ApiKey, TimeSpan.FromSeconds(10), null, CancellationToken.None).ConfigureAwait(false);
            Expect(!ok, $"a wrong key should fail the connection test: {detail}");
        }).ConfigureAwait(false);

        await StepAsync("connection-test", "The connection test (and the heartbeat) with the right API key.", async () =>
        {
            var (ok, detail) = await ManagerHealthProbe.ProbeAsync(_transport, _connection.Name, _connection.Kind, _connection.BaseUrl, _connection.ApiKey, TimeSpan.FromSeconds(10), null, CancellationToken.None).ConfigureAwait(false);
            Expect(ok, $"the connection test should pass: {detail}");
        }).ConfigureAwait(false);

        await StepAsync("describe-wrong-key", "Asking which folders the app manages with an API key the app does not know.", async () =>
        {
            var wrong = _connection with { ApiKey = "00000000000000000000000000000000" };
            var description = await _port.DescribeAsync(wrong).ConfigureAwait(false);
            Expect(description.Status == SignalStatus.Unreachable, $"a wrong key should read as unreachable, was {description.Status}");
        }).ConfigureAwait(false);

        await StepAsync("describe-no-root-folder", "Asking a fresh app which folders it manages, before it has a root folder.", async () =>
        {
            var description = await _port.DescribeAsync(_connection).ConfigureAwait(false);
            Expect(description.Status == SignalStatus.Reported && description.LibraryRoots.Count == 0, "a fresh app should report no folders");
        }).ConfigureAwait(false);
    }

    private async Task QueueSteps(WebhookReceiver receiver)
    {
        await StepAsync("queue-empty", "The queue of an app that is downloading nothing.", async () =>
        {
            var signal = await _port.QueueRowsAsync(_connection).ConfigureAwait(false);
            Expect(signal.Status == SignalStatus.Reported && signal.Rows.Count == 0, "the queue should be empty");
        }).ConfigureAwait(false);

        IReadOnlyList<ManagerQueueRow> rows = [];
        await PushReleaseAsync().ConfigureAwait(false);
        await StepAsync("queue-with-download", "The queue while the app has one release handed to its download client.", async () =>
        {
            var signal = await _port.QueueRowsAsync(_connection).ConfigureAwait(false);
            Expect(signal.Status == SignalStatus.Reported && signal.Rows.Count == 1, $"the queue should hold one item, held {signal.Rows.Count}");
            rows = signal.Rows;
            _downloadId = ManagerValues.Text(signal.Rows[0].Payload.Get("downloadId"));
        }).ConfigureAwait(false);
        Expect(await receiver.WaitForAsync("Grab", TimeSpan.FromSeconds(90)).ConfigureAwait(false), "the app never posted its Grab webhook");

        await StepAsync("queue-remove-unknown-item", "Asking the app to remove a queue item it does not have.", async () =>
        {
            try
            {
                await _port.RemoveQueueItemAsync(_connection, new WireObject().Set("id", 987654L)).ConfigureAwait(false);
                Expect(false, "removing an unknown queue item should be refused");
            }
            catch (MediaManagerHttpException exception)
            {
                Say($"  refused as expected: {exception.Message}");
            }
        }).ConfigureAwait(false);

        await StepAsync("queue-remove-and-blocklist", "Removing the queue item from the download client and blocklisting the release.", async () =>
        {
            Expect(rows.Count == 1, "there is no queue item to remove");
            await _port.RemoveQueueItemAsync(_connection, rows[0].Payload).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await TryAsync("wait for the blocklist", () => Wait.UntilAsync("the blocklist to hold the release", TimeSpan.FromSeconds(60), async () => ((await _api.GetAsync("/api/v3/blocklist").ConfigureAwait(false))?["totalRecords"]?.GetValue<int>() ?? 0) > 0)).ConfigureAwait(false);

        await StepAsync("queue-after-remove", "The queue once the item has been removed.", async () =>
        {
            var signal = await _port.QueueRowsAsync(_connection).ConfigureAwait(false);
            Expect(signal.Status == SignalStatus.Reported && signal.Rows.Count == 0, "the queue should be empty again");
        }).ConfigureAwait(false);
    }

    private async Task ImportSteps(WebhookReceiver receiver)
    {
        MakeDownloadedFiles();
        var scan = new ArrDownloadedScan(_transport);

        // The app imports the file it is told about; the others are handed over without being recorded.
        foreach (var index in Enumerable.Range(1, _flavour.ImportNames.Count - 1))
        {
            await TryAsync("hand over another file", () => _api.PostAsync(
                DownloadedScanRules.CommandPath,
                new System.Text.Json.Nodes.JsonObject { ["name"] = DownloadedScanRules.CommandName(_flavour.Scope), ["path"] = FileInDownloads(index), ["importMode"] = DownloadedScanRules.ImportMode })).ConfigureAwait(false);
        }

        await StepAsync("downloaded-scan", "Weir asks the app to scan the folder a finished download is in; the app imports it.", async () =>
        {
            var result = await scan.RequestAsync(_connection, _flavour.Scope, FileInDownloads(0), null).ConfigureAwait(false);
            Expect(result.Accepted, $"the scan was refused: {result.FailureReason}");
        }).ConfigureAwait(false);
        await WaitForImportAsync().ConfigureAwait(false);
        Expect(await receiver.WaitForAsync("Download", TimeSpan.FromSeconds(90)).ConfigureAwait(false), "the app never posted its Download webhook");

        await AddRemotePathMappingAsync().ConfigureAwait(false);
        await StepAsync("setup-check", "What Weir reads to judge a workflow's setup: the path mappings, the download clients and their settings, the queue.", async () =>
        {
            var client = new MediaManagerHttpClient(_settings.Url, _apiKey, _transport, TimeSpan.FromSeconds(30));
            var mappings = await client.GetJsonAsync(ManagerSetupRules.RemotePathMappingPath).ConfigureAwait(false);
            Expect(ManagerSetupRules.ParseMappings(mappings).Count == 1, "Weir should read the one mapping");
            var clients = await client.GetJsonAsync(ManagerSetupRules.DownloadClientPath).ConfigureAwait(false);
            Expect(ManagerSetupRules.ParseDownloadClients(clients, _flavour.Scope).Count == 1, "Weir should read the one download client");
            await client.GetJsonAsync(ManagerSetupRules.DownloadClientConfigPath).ConfigureAwait(false);
            await _port.QueueRowsAsync(_connection).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await StepAsync("downloaded-scan-with-mapping", "The same scan once the app has a remote path mapping that covers the folder: Weir sends the path the mapping gives.", async () =>
        {
            var result = await scan.RequestAsync(_connection, _flavour.Scope, FileInDownloads(0), null).ConfigureAwait(false);
            Expect(result.Accepted, $"the scan was refused: {result.FailureReason}");
        }).ConfigureAwait(false);

        await StepAsync("downloaded-scan-for-download", "The same scan for a download the download client named, which Weir sends as the client's own id for it, as text.", async () =>
        {
            Expect(_downloadId is not null, "the queue item carried no downloadId");
            var result = await scan.RequestAsync(_connection, _flavour.Scope, FileInDownloads(0), _downloadId).ConfigureAwait(false);
            Expect(result.Accepted, $"the scan was refused: {result.FailureReason}");
        }).ConfigureAwait(false);

        await StepAsync("downloaded-scan-refuses-a-number", "The scan command with the download client's id as a number, which the app answers 500 to; Weir sends text.", async () =>
        {
            var client = new MediaManagerHttpClient(_settings.Url, _apiKey, _transport, TimeSpan.FromSeconds(30));
            var command = DownloadedScanRules.CommandBody(_flavour.Scope, FileInDownloads(0), null).Set("downloadClientId", 1L);
            try
            {
                await client.PostJsonAsync(DownloadedScanRules.CommandPath, command).ConfigureAwait(false);
                Expect(false, "the app should refuse a number");
            }
            catch (MediaManagerHttpException exception)
            {
                Say($"  refused as expected: {exception.Message}");
            }
        }).ConfigureAwait(false);
    }

    private async Task LibrarySteps()
    {
        IReadOnlyList<ManagerLibraryFile> files = [];
        await StepAsync("library-listing", "Weir lists the files the app keeps, matched to titles, and asks which files it still keeps.", async () =>
        {
            var listing = await _port.ListLibraryFilesAsync(_connection, _flavour.Scope).ConfigureAwait(false);
            Expect(listing.Status == SignalStatus.Reported && listing.Files.Count == _flavour.ImportNames.Count, $"the listing should hold {_flavour.ImportNames.Count} file(s), was {listing.Status} with {listing.Files.Count}");
            files = listing.Files;
            var truth = await _port.LibraryTruthAsync(_connection, _flavour.Scope).ConfigureAwait(false);
            Say($"  library truth: {truth.Status}, {truth.LibraryFilePaths.Count} file(s)");
        }).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return;
        }

        _filePath = files[0].FilePath;
        _titleId = long.Parse(files[0].TitleId, System.Globalization.CultureInfo.InvariantCulture);

        await StepAsync("redownload-risk", "The two reads that decide whether a clean could make the app download the title again: the file and its quality profile.", async () =>
        {
            var gateway = new ArrRedownloadRiskGateway(_transport);
            await gateway.GetFileFormatSnapshotAsync(_connection, _flavour.Scope, files[0].FileId!.Value).ConfigureAwait(false);
            await gateway.GetQualityProfileAsync(_connection, files[0].QualityProfileId ?? _qualityProfileId).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await StepAsync("rescan", "Weir tells the app a file in its library changed, so it reads the title's folder again.", async () =>
        {
            var outcome = await _port.FileChangedAsync(_connection, new HashSet<string>(), files[0].TitleId, _filePath, "recorded").ConfigureAwait(false);
            Expect(outcome == ManagerNotifyOutcome.Notified, "the rescan should be accepted");
        }).ConfigureAwait(false);
    }

    private async Task RedownloadSteps(WebhookReceiver receiver)
    {
        if (_filePath.Length == 0)
        {
            Fail("redownload", new RecorderException("there is no library file to download again"));
            return;
        }

        var redownload = new ArrManagerRedownload(_transport);
        await StepAsync("redownload-unknown-title", "Download again for a title the app does not have.", async () =>
        {
            try
            {
                var result = await redownload.RequestRedownloadAsync(_connection, _flavour.Scope, "987654", _filePath).ConfigureAwait(false);
                Say($"  answered: {result.Outcome}");
            }
            catch (MediaManagerHttpException exception)
            {
                Say($"  refused as expected: {exception.Message}");
            }
        }).ConfigureAwait(false);

        await StepAsync("redownload", "Download again: the app deletes the file, then is asked to search for the title.", async () =>
        {
            var result = await redownload.RequestRedownloadAsync(_connection, _flavour.Scope, _titleId.ToString(System.Globalization.CultureInfo.InvariantCulture), _filePath).ConfigureAwait(false);
            Expect(result.Outcome == RedownloadOutcome.Requested && result.DeletedExistingFile, $"the redownload should delete the file and ask for a search, was {result.Outcome}");
        }).ConfigureAwait(false);
        Expect(await receiver.WaitForAsync(_flavour.DeleteEvent, TimeSpan.FromSeconds(90)).ConfigureAwait(false), $"the app never posted its {_flavour.DeleteEvent} webhook");

        await StepAsync("redownload-file-gone", "Download again once the file has already been deleted.", async () =>
        {
            var result = await redownload.RequestRedownloadAsync(_connection, _flavour.Scope, _titleId.ToString(System.Globalization.CultureInfo.InvariantCulture), _filePath).ConfigureAwait(false);
            Say($"  answered: {result.Outcome}");
        }).ConfigureAwait(false);
    }

    /// <summary>A finished download, as the app sees it.</summary>
    private string FileInDownloads(int index) => $"/downloads/{_flavour.ImportNames[index]}/{_flavour.ImportNames[index]}.mkv";

    private void WriteWebhooks(WebhookReceiver receiver)
    {
        foreach (var (eventType, exchange) in receiver.FirstOfEachType())
        {
            var state = "webhook-" + Regex.Replace(eventType, "(?<=[a-z])(?=[A-Z])", "-").ToLowerInvariant();
            _writer.Write(state, $"The app's own {eventType} webhook, as it posted it. The 200 is the recorder's, not Weir's.", [exchange]);
            Say($"recorded {state}");
        }
    }

    private async Task<string> ReadApiKeyAsync()
    {
        var path = Path.Combine(_settings.ConfigFolder, "config.xml");
        string? key = null;
        await Wait.UntilAsync("the app to write its config.xml", TimeSpan.FromMinutes(4), () =>
        {
            if (File.Exists(path) && Regex.Match(File.ReadAllText(path), "<ApiKey>([^<]+)</ApiKey>") is { Success: true } match)
            {
                key = match.Groups[1].Value;
            }

            return Task.FromResult(key is not null);
        }).ConfigureAwait(false);
        return key!;
    }

    private async Task StepAsync(string state, string note, Func<Task> run)
    {
        _log.Clear();
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fail(state, exception);
            return;
        }

        var exchanges = _log.Snapshot();
        if (exchanges.Count == 0)
        {
            Fail(state, new RecorderException("Weir made no call"));
            return;
        }

        _writer.Write(state, note, exchanges);
        Say($"recorded {state}: {exchanges.Count} exchange(s)");
    }

    private void Expect(bool condition, string problem)
    {
        if (!condition)
        {
            _failures.Add(problem);
            Say($"  NOT AS EXPECTED: {problem}");
        }
    }

    private void Fail(string step, Exception exception)
    {
        _failures.Add($"{step}: {exception.Message}");
        Say($"FAILED {step}: {exception}");
    }

    private static void Say(string line) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}");
}
