using System.Text.Json;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// The downloaded-scan hand-back and the setup check, against what a real Sonarr and a real Radarr answered
/// (<c>fixtures/recorded/sonarr</c> and <c>radarr</c>). The one fact that only a real app could teach: the download client's id in the
/// scan command is text, and both apps answer 500 to a number.
/// </summary>
public sealed class ArrScanAndSetupRepliesTests
{
    public static TheoryData<string> Apps => ["sonarr", "radarr"];

    private static string ScopeOf(string app) => app == "sonarr" ? MediaManagerKinds.Tv : MediaManagerKinds.Movie;

    private static string CommandName(string app) => app == "sonarr" ? "DownloadedEpisodesScan" : "DownloadedMoviesScan";

    /// <summary>The path Weir had written the file to, as the recording of the plain scan sent it.</summary>
    private static string WrittenPath(string app) => (string)ArrRecording.Load(app, "downloaded-scan").To("POST", "/api/v3/command").Request.Body!["path"]!;

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_scan_with_no_mapping_asks_the_app_to_move_in_the_file_where_Weir_wrote_it(string app)
    {
        var recording = ArrRecording.Load(app, "downloaded-scan");
        using var replay = new ArrReplay(app, recording);

        var result = await new ArrDownloadedScan(replay).RequestAsync(replay.Connection(), ScopeOf(app), WrittenPath(app), downloadId: null);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal(201, recording.To("POST", "/api/v3/command").Reply.Status);
        Assert.Equal(CommandName(app), (string)recording.To("POST", "/api/v3/command").Reply.Body!["name"]!);
        Assert.Empty(recording.To("GET", "/api/v3/remotepathmapping").Reply.Body!.AsArray());
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_mapping_that_covers_the_folder_changes_the_path_the_app_is_told_to_look_in(string app)
    {
        var recording = ArrRecording.Load(app, "downloaded-scan-with-mapping");
        using var replay = new ArrReplay(app, recording);
        var mapping = recording.To("GET", "/api/v3/remotepathmapping").Reply.Body!.AsArray().Single()!.AsObject();

        var result = await new ArrDownloadedScan(replay).RequestAsync(replay.Connection(), ScopeOf(app), WrittenPath(app), downloadId: null);

        Assert.True(result.Accepted, result.FailureReason);
        var sent = (string)recording.To("POST", "/api/v3/command").Request.Body!["path"]!;
        Assert.StartsWith(((string)mapping["remotePath"]!).TrimEnd('/'), sent, StringComparison.Ordinal);
        Assert.DoesNotContain((string)mapping["localPath"]!.ToString().TrimEnd('/') + "/", sent, StringComparison.Ordinal);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_download_the_client_named_is_sent_as_text_which_the_app_accepts(string app)
    {
        var recording = ArrRecording.Load(app, "downloaded-scan-for-download");
        using var replay = new ArrReplay(app, recording);
        var downloadId = (string)recording.To("POST", "/api/v3/command").Request.Body!["downloadClientId"]!;

        var result = await new ArrDownloadedScan(replay).RequestAsync(replay.Connection(), ScopeOf(app), WrittenPath(app), downloadId);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.IsType<WireString>(result.Command["downloadClientId"]);
        Assert.Equal(downloadId, WireConvert.Str(result.Command["downloadClientId"]));
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task The_app_refuses_a_download_client_id_that_is_a_number_and_Weir_never_sends_one(string app)
    {
        var recording = ArrRecording.Load(app, "downloaded-scan-refuses-a-number");
        var recorded = recording.Only;
        using var replay = new ArrReplay(app, recording);
        var client = new MediaManagerHttpClient("http://localhost:8989", "a-key-the-recording-never-saw", replay);

        var refused = await Assert.ThrowsAsync<MediaManagerHttpException>(
            () => client.PostJsonAsync(DownloadedScanRules.CommandPath, (WireObject)WireJsonParser.Parse(recorded.Request.Body!.ToJsonString())!));

        Assert.Equal(JsonValueKind.Number, recorded.Request.Body!["downloadClientId"]!.GetValueKind());
        Assert.Equal(500, recorded.Reply.Status);
        Assert.Equal(500, refused.StatusCode);
        var weirs = DownloadedScanRules.CommandBody(ScopeOf(app), "/downloads/x.mkv", "1");
        Assert.IsType<WireString>(weirs["downloadClientId"]);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void The_setup_check_reads_the_mapping_the_download_client_and_the_completed_download_setting_the_app_reported(string app)
    {
        var recording = ArrRecording.Load(app, "setup-check");
        var mappingReply = recording.To("GET", ManagerSetupRules.RemotePathMappingPath).Reply;
        var clientReply = recording.To("GET", ManagerSetupRules.DownloadClientPath).Reply;
        var configReply = recording.To("GET", ManagerSetupRules.DownloadClientConfigPath).Reply;

        var mappings = ManagerSetupRules.ParseMappings(WireJsonParser.Parse(mappingReply.Text));
        var clients = ManagerSetupRules.ParseDownloadClients(WireJsonParser.Parse(clientReply.Text), ScopeOf(app));

        var mapping = Assert.Single(mappings);
        Assert.Equal(("transmission", "/remote-downloads/", "/downloads/"), (mapping.Host, mapping.RemotePath, mapping.LocalPath));
        var client = Assert.Single(clients);
        Assert.Equal(("Transmission", "Transmission", true, "transmission", true, 9091), (client.Name, client.Implementation, client.Enabled, client.Host, client.IsTorrent, client.Port));
        var completed = configReply.Body!["enableCompletedDownloadHandling"]!.GetValue<bool>();
        Assert.True(completed);

        var result = ManagerSetupRules.EvaluateArr(
            "the app", ScopeOf(app), "/remote-downloads", "/downloads", mappings, clients, completed, removesOriginals: false);

        Assert.Equal(["transmission"], result.Hosts);
        Assert.DoesNotContain(result.Lines, line => line.State == SetupCheckLine.Problem);
        Assert.Contains(result.Lines, line => line is { State: SetupCheckLine.Ok } && line.Text.Contains("Completed Download Handling is on", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void Without_the_mapping_the_same_setup_is_a_problem_Weir_names(string app)
    {
        var recording = ArrRecording.Load(app, "setup-check");
        var clients = ManagerSetupRules.ParseDownloadClients(WireJsonParser.Parse(recording.To("GET", ManagerSetupRules.DownloadClientPath).Reply.Text), ScopeOf(app));

        var result = ManagerSetupRules.EvaluateArr("the app", ScopeOf(app), "/remote-downloads", "/downloads", [], clients, completedDownloadHandling: true);

        Assert.Contains(result.Lines, line => line.State == SetupCheckLine.Problem);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task The_setup_check_asks_for_exactly_what_the_recording_shows(string app)
    {
        var recording = ArrRecording.Load(app, "setup-check");
        using var replay = new ArrReplay(app, recording);
        var client = new MediaManagerHttpClient("http://localhost:8989", "a-key-the-recording-never-saw", replay);

        await client.GetJsonAsync(ManagerSetupRules.RemotePathMappingPath);
        await client.GetJsonAsync(ManagerSetupRules.DownloadClientPath);
        await client.GetJsonAsync(ManagerSetupRules.DownloadClientConfigPath);
        await replay.Port().QueueRowsAsync(replay.Connection());

        replay.AssertSentAsRecorded(recording);
    }
}
