using System.Text.Json.Nodes;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// The library listing, the rescan, the re-download risk reads and "Download again", against what a real Sonarr and a real Radarr
/// answered (<c>fixtures/recorded/sonarr</c> and <c>radarr</c>). The destructive step is the one to hold: the file is deleted
/// only when the app listed it, and only the file asked for.
/// </summary>
public sealed class ArrLibraryAndRedownloadRepliesTests
{
    public static TheoryData<string> Apps => ["sonarr", "radarr"];

    private static string ScopeOf(string app) => app == "sonarr" ? MediaManagerKinds.Tv : MediaManagerKinds.Movie;

    /// <summary>The files the recorded app listed, as (title id, title, path, file id, quality profile id), read straight from its reply.</summary>
    private static List<(string TitleId, string Title, string Path, long FileId, long ProfileId)> ListedBy(string app)
    {
        var recording = ArrRecording.Load(app, "library-listing");
        if (app == "radarr")
        {
            return [.. recording.Exchanges[0].Reply.Body!.AsArray().Select(movie => movie!.AsObject())
                .Where(movie => movie["movieFile"] is JsonObject)
                .Select(movie => (movie["id"]!.ToJsonString(), (string)movie["title"]!, (string)movie["movieFile"]!["path"]!, (long)movie["movieFile"]!["id"]!, (long)movie["qualityProfileId"]!))];
        }

        var series = recording.To("GET", "/api/v3/series").Reply.Body!.AsArray().Select(row => row!.AsObject()).ToList();
        return [.. series.SelectMany(row => recording.Exchanges
            .First(exchange => exchange.Request.Path == "/api/v3/episodefile" && exchange.Request.Query == $"seriesId={row["id"]!.ToJsonString()}" && exchange.Reply.Status == 200)
            .Reply.Body!.AsArray().Select(file => file!.AsObject())
            .Select(file => (row["id"]!.ToJsonString(), (string)row["title"]!, (string)file["path"]!, (long)file["id"]!, (long)row["qualityProfileId"]!)))];
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task Each_file_the_app_keeps_is_matched_to_the_title_it_belongs_to(string app)
    {
        var recording = ArrRecording.Load(app, "library-listing");
        using var replay = new ArrReplay(app, recording);
        var expected = ListedBy(app);

        var signal = await replay.Port().ListLibraryFilesAsync(replay.Connection(), ScopeOf(app));

        Assert.Equal(SignalStatus.Reported, signal.Status);
        Assert.NotEmpty(expected);
        Assert.Equal(
            expected.OrderBy(file => file.Path, StringComparer.Ordinal),
            signal.Files.Select(file => (file.TitleId, file.TitleName, file.FilePath, file.FileId!.Value, file.QualityProfileId!.Value)).OrderBy(file => file.FilePath, StringComparer.Ordinal));
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task The_paths_the_app_still_keeps_are_the_paths_it_listed(string app)
    {
        var recording = ArrRecording.Load(app, "library-listing");
        using var replay = new ArrReplay(app, recording);

        var truth = await replay.Port().LibraryTruthAsync(replay.Connection(), ScopeOf(app));

        Assert.Equal(SignalStatus.Reported, truth.Status);
        Assert.Equal(ListedBy(app).Select(file => file.Path).Order(StringComparer.Ordinal), truth.LibraryFilePaths.Order(StringComparer.Ordinal));
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_changed_file_makes_the_app_read_its_title_again(string app)
    {
        var recording = ArrRecording.Load(app, "rescan");
        using var replay = new ArrReplay(app, recording);
        var command = recording.To("POST", "/api/v3/command");
        var titleId = command.Request.Body![app == "sonarr" ? "seriesId" : "movieId"]!.ToJsonString();

        var outcome = await replay.Port().FileChangedAsync(replay.Connection(), new HashSet<string>(), titleId, ListedBy(app)[0].Path, "recorded");

        Assert.Equal(ManagerNotifyOutcome.Notified, outcome);
        Assert.Equal(201, command.Reply.Status);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_files_format_and_its_quality_profile_are_read_from_the_apps_own_replies(string app)
    {
        var recording = ArrRecording.Load(app, "redownload-risk");
        using var replay = new ArrReplay(app, recording);
        var filePath = recording.Exchanges[0].Request.Path;
        var fileId = long.Parse(filePath[(filePath.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var profileRoute = recording.Exchanges[1].Request.Path;
        var profileId = long.Parse(profileRoute[(profileRoute.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var gateway = new ArrRedownloadRiskGateway(replay);

        var snapshot = await gateway.GetFileFormatSnapshotAsync(replay.Connection(), ScopeOf(app), fileId);
        var profile = await gateway.GetQualityProfileAsync(replay.Connection(), profileId);

        var recordedFile = recording.Exchanges[0].Reply.Body!;
        var recordedProfile = recording.Exchanges[1].Reply.Body!;
        Assert.Equal(["eng"], snapshot.AudioLanguages);
        Assert.Equal((int)recordedFile["customFormatScore"]!, snapshot.CustomFormatScore);
        Assert.Equal(recordedFile["customFormats"]!.AsArray().Count, snapshot.CustomFormats.Count);
        Assert.Equal((bool)recordedProfile["upgradeAllowed"]!, profile.UpgradeAllowed);
        Assert.Equal((int)recordedProfile["cutoffFormatScore"]!, profile.CutoffFormatScore);
        Assert.Equal(recordedProfile["formatItems"]!.AsArray().Count, profile.FormatScores.Count);
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task Download_again_deletes_the_one_file_asked_for_and_then_asks_the_app_to_search_for_the_title(string app)
    {
        var recording = ArrRecording.Load(app, "redownload");
        using var replay = new ArrReplay(app, recording);
        var asked = RecordedRequestFor(recording);

        var result = await new ArrManagerRedownload(replay).RequestRedownloadAsync(replay.Connection(), ScopeOf(app), asked.TitleId, asked.Path);

        Assert.Equal(RedownloadOutcome.Requested, result.Outcome);
        Assert.True(result.DeletedExistingFile);
        Assert.Equal(asked.Size, result.ExistingFileSizeBytes);
        var deletes = replay.Sent.Where(sent => sent.Method == "DELETE").ToList();
        var delete = Assert.Single(deletes);
        Assert.Equal(recording.Exchanges.Single(exchange => exchange.Request.Method == "DELETE").Request.Path, delete.Path);
        Assert.Equal(["GET", "DELETE", "POST"], replay.Sent.Select(sent => sent.Method));
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task A_title_the_app_does_not_have_deletes_nothing(string app)
    {
        var recording = ArrRecording.Load(app, "redownload-unknown-title");
        using var replay = new ArrReplay(app, recording);
        var titleId = app == "sonarr" ? recording.Only.Request.Query["seriesId=".Length..] : recording.Only.Request.Path[(recording.Only.Request.Path.LastIndexOf('/') + 1)..];
        var path = ListedBy(app)[0].Path;
        var redownload = new ArrManagerRedownload(replay);

        if (app == "radarr")
        {
            var refused = await Assert.ThrowsAsync<MediaManagerHttpException>(() => redownload.RequestRedownloadAsync(replay.Connection(), ScopeOf(app), titleId, path));
            Assert.Equal(404, refused.StatusCode);
        }
        else
        {
            var result = await redownload.RequestRedownloadAsync(replay.Connection(), ScopeOf(app), titleId, path);
            Assert.Equal(RedownloadOutcome.FileNotMatched, result.Outcome);
            Assert.False(result.DeletedExistingFile);
        }

        Assert.DoesNotContain(replay.Sent, sent => sent.Method is "DELETE" or "POST");
        replay.AssertSentAsRecorded(recording);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task Asking_again_once_the_file_is_gone_does_not_delete_anything_twice(string app)
    {
        var recording = ArrRecording.Load(app, "redownload-file-gone");
        var gone = ArrRecording.Load(app, "redownload");
        using var replay = new ArrReplay(app, recording);
        var asked = RecordedRequestFor(gone);

        var result = await new ArrManagerRedownload(replay).RequestRedownloadAsync(replay.Connection(), ScopeOf(app), asked.TitleId, asked.Path);

        Assert.DoesNotContain(replay.Sent, sent => sent.Method == "DELETE");
        if (app == "radarr")
        {
            Assert.Equal(RedownloadOutcome.Requested, result.Outcome);
            Assert.False(result.DeletedExistingFile);
            Assert.Contains(replay.Sent, sent => sent is { Method: "POST", Path: "/api/v3/command" });
        }
        else
        {
            Assert.Equal(RedownloadOutcome.FileNotMatched, result.Outcome);
            Assert.DoesNotContain(replay.Sent, sent => sent.Method == "POST");
        }

        replay.AssertSentAsRecorded(recording);
    }

    /// <summary>The title, file and size a recorded "Download again" was made for, read from what the app was asked and answered.</summary>
    private static (string TitleId, string Path, long Size) RecordedRequestFor(ArrRecording recording)
    {
        var first = recording.Exchanges[0];
        if (first.Request.Path.StartsWith("/api/v3/movie/", StringComparison.Ordinal))
        {
            var movie = first.Reply.Body!;
            return (first.Request.Path[(first.Request.Path.LastIndexOf('/') + 1)..], (string)movie["movieFile"]!["path"]!, (long)movie["movieFile"]!["size"]!);
        }

        var deleted = recording.Exchanges.Single(exchange => exchange.Request.Method == "DELETE").Request.Path;
        var file = first.Reply.Body!.AsArray().Select(item => item!.AsObject()).Single(item => item["id"]!.ToJsonString() == deleted[(deleted.LastIndexOf('/') + 1)..]);
        return (first.Request.Query["seriesId=".Length..], (string)file["path"]!, (long)file["size"]!);
    }
}
