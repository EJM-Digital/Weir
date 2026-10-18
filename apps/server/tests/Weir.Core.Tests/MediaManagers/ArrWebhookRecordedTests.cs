using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// How Weir reads the webhooks a real Sonarr and a real Radarr posted (<c>fixtures/recorded/sonarr/*/webhook-*.json</c>, as the app sent
/// them): only the import is an event to act on, and it carries the file the app imported from, which is how Weir knows an import is
/// of the copy it handed back.
/// </summary>
public sealed class ArrWebhookRecordedTests
{
    public static TheoryData<string, string> Ignored
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var app in new[] { "sonarr", "radarr" })
            {
                foreach (var state in ArrRecording.States(app, "webhook-").Where(state => state != "webhook-download"))
                {
                    data.Add(app, state);
                }
            }

            return data;
        }
    }

    private static MediaManagerImportEvent? Read(string app, ArrRecording recording) =>
        ImportEvents.DialectForSource(app)!.Normalize((WireObject)WireJsonParser.Parse(recording.Only.Request.Body!.ToJsonString())!);

    [Theory]
    [MemberData(nameof(Ignored))]
    public void Every_event_but_the_import_is_not_work_for_Weir(string app, string state)
    {
        var recording = ArrRecording.Load(app, state);

        Assert.NotEqual("Download", (string)recording.Only.Request.Body!["eventType"]!);
        Assert.Null(Read(app, recording));
    }

    [Fact]
    public void A_sonarr_import_names_the_episode_the_file_in_its_library_and_the_file_it_came_from()
    {
        var recording = ArrRecording.Load("sonarr", "webhook-download");
        var body = recording.Only.Request.Body!;
        var episode = body["episodes"]![0]!;

        var imported = Read("sonarr", recording)!;

        Assert.Equal("Download", (string)body["eventType"]!);
        Assert.Equal(("sonarr", MediaManagerImportEvent.Imported, MediaManagerKinds.Tv), (imported.SourceKey, imported.EventKind, imported.MediaScope));
        Assert.Equal((string)body["episodeFile"]!["path"]!, imported.FilePath);
        Assert.Equal((string)body["episodeFile"]!["sourcePath"]!, imported.SourcePath);
        Assert.Equal((string)body["series"]!["title"]!, imported.ShowTitle);
        Assert.Equal((int)episode["seasonNumber"]!, (int)imported.SeasonNumber!.Value);
        Assert.Equal((int)episode["episodeNumber"]!, (int)imported.EpisodeNumber!.Value);
        Assert.Equal((string)episode["title"]!, imported.EpisodeTitle);
        Assert.Equal((long)episode["id"]!, (long)imported.SourceEntityId!.Value);
        Assert.Null(imported.DownloadId);
        Assert.NotNull(imported.Artwork);
    }

    [Fact]
    public void A_radarr_import_names_the_movie_the_file_in_its_library_and_the_file_it_came_from()
    {
        var recording = ArrRecording.Load("radarr", "webhook-download");
        var body = recording.Only.Request.Body!;

        var imported = Read("radarr", recording)!;

        Assert.Equal("Download", (string)body["eventType"]!);
        Assert.Equal(("radarr", MediaManagerImportEvent.Imported, MediaManagerKinds.Movie), (imported.SourceKey, imported.EventKind, imported.MediaScope));
        Assert.Equal((string)body["movieFile"]!["path"]!, imported.FilePath);
        Assert.Equal((string)body["movieFile"]!["sourcePath"]!, imported.SourcePath);
        Assert.Equal((string)body["movie"]!["title"]!, imported.Title);
        Assert.Equal((int)body["movie"]!["year"]!, (int)imported.Year!.Value);
        Assert.Equal((long)body["movie"]!["id"]!, (long)imported.SourceEntityId!.Value);
        Assert.Null(imported.DownloadId);
        Assert.NotNull(imported.Artwork);
    }

    [Theory]
    [InlineData("sonarr")]
    [InlineData("radarr")]
    public void The_import_names_the_file_it_came_from_as_the_path_Weir_asked_the_app_to_scan(string app)
    {
        var import = ArrRecording.Load(app, "webhook-download").Only.Request.Body!;
        var scanned = (string)ArrRecording.Load(app, "downloaded-scan").To("POST", DownloadedScanRules.CommandPath).Request.Body!["path"]!;
        var file = import[app == "sonarr" ? "episodeFile" : "movieFile"]!;

        Assert.Equal(scanned, (string)file["sourcePath"]!);
        Assert.NotEqual(scanned, (string)file["path"]!);
    }
}
