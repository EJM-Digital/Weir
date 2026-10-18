using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Tests.Shared;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// "Download again" through a running Weir, against what a real Sonarr and a real Radarr answered (<c>fixtures/recorded/sonarr</c> and
/// <c>radarr</c>). A library scan lists the manager's files and matches the one on disk to its title; the person's request then makes the
/// manager delete that file and search for the title. The fake manager answers each call with the recorded reply, and the test holds that
/// Weir asked for exactly what the recording shows it asked. The one thing changed in a recorded reply is the path of the file, to the one
/// the test wrote under its own folder: the apps' paths are their own (<c>/tv/...</c>, <c>/movies/...</c>).
/// </summary>
[ContractArea("processing")]
public sealed class RecordedRedownloadTests
{
    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    public async Task Download_again_makes_the_app_delete_the_matched_file_and_search_as_recorded(string app)
    {
        var redownload = ArrRecording.Load(app, "redownload");
        var listing = ArrRecording.Load(app, "library-listing");
        await using var scenario = await Scenario.StartAsync();
        var (fake, library) = app == "radarr" ? await scenario.RadarrSetupAsync() : await scenario.SonarrSetupAsync();
        var libraryId = (int)library["id"]!;
        var (file, relative) = WriteLibraryFile(scenario, app);
        ServeRecorded(fake, app, listing, redownload, file);

        var folders = await scenario.Admin.PutWithCsrfAsync(
            $"{WeirClient.Api}/processing/libraries/{libraryId}/library-settings",
            new JsonObject { ["library_folders"] = new JsonArray(Path.Combine(scenario.Root, "library")) });
        Assert.True(folders.Status == HttpStatusCode.OK, folders.ToString());
        var scan = await scenario.Admin.PostWithCsrfAsync($"{WeirClient.Api}/processing/libraries/{libraryId}/library-scan", new JsonObject());
        Assert.True(scan.Status is HttpStatusCode.OK or HttpStatusCode.Accepted, scan.ToString());
        var matched = await scenario.WaitWhileWorkedOnAsync(
            async () =>
            {
                var files = await scenario.Admin.GetAsync($"{WeirClient.Api}/processing/libraries/{libraryId}/library-files");
                return files.Fields["files"]?.AsArray().Select(row => row!.AsObject()).FirstOrDefault(row => (string?)row["manager_title"] is not null);
            },
            "the library scan to match the file to its title in the manager");

        var asked = await scenario.Admin.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/libraries/{libraryId}/library-redownloads",
            new JsonObject { ["path"] = (string)matched["path"]!, ["confirm_destructive"] = true });

        Assert.True(asked.Status == HttpStatusCode.OK, asked.ToString());
        Assert.Equal("requested", (string)asked.Fields["outcome"]!);
        var deletes = redownload.Exchanges.Where(exchange => exchange.Request.Method == "DELETE").ToList();
        var delete = Assert.Single(deletes);
        Assert.Single(fake.RequestsTo("DELETE", delete.Request.Path));
        var search = redownload.To("POST", "/api/v3/command").Request.Body!;
        var searches = fake.RequestsTo("POST", "/api/v3/command").Where(request => (string?)request.Json?["name"] == (string)search["name"]!).ToList();
        var sent = Assert.Single(searches);
        Assert.True(JsonNode.DeepEquals(search, sent.Json), $"Weir asked for {sent.Json?.ToJsonString()}; the recording shows {search.ToJsonString()}.");
        Assert.Equal(fake.ApiKey, sent.Header("X-Api-Key"));
        Assert.EndsWith(relative.Replace('\\', '/'), ((string)matched["path"]!).Replace('\\', '/'), StringComparison.Ordinal);
    }

    /// <summary>The file the manager lists, written where the scan looks, and the path the scan will call it.</summary>
    private static (string File, string Relative) WriteLibraryFile(Scenario scenario, string app)
    {
        var relative = app == "radarr" ? Path.Combine("Big Buck Bunny (2008)", "Big.Buck.Bunny.2008.mkv") : Path.Combine("Breaking Bad", "Season 1", "Breaking.Bad.S01E01.mkv");
        var file = Path.Combine(scenario.Root, "library", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng"])));
        return (file, relative);
    }

    /// <summary>Makes the fake answer the library scan and the redownload with the recorded replies, the file at <paramref name="file"/>.</summary>
    private static void ServeRecorded(FakeManager fake, string app, ArrRecording listing, ArrRecording redownload, string file)
    {
        var deleted = redownload.Exchanges.Single(exchange => exchange.Request.Method == "DELETE").Request.Path;
        var deletedId = deleted[(deleted.LastIndexOf('/') + 1)..];
        foreach (var exchange in listing.Exchanges.Concat(redownload.Exchanges))
        {
            var body = exchange.Reply.Body?.DeepClone();
            if (app == "radarr" && body is JsonArray movies && exchange.Request.Path == "/api/v3/movie")
            {
                movies[0]!["movieFile"]!["path"] = file;
            }
            else if (app == "radarr" && body is JsonObject movie && movie["movieFile"] is JsonObject)
            {
                movie["movieFile"]!["path"] = file;
            }
            else if (app == "sonarr" && body is JsonArray episodes && exchange.Request.Path == "/api/v3/episodefile")
            {
                foreach (var episode in episodes.Select(item => item!.AsObject()).Where(item => item["id"]!.ToJsonString() == deletedId))
                {
                    episode["path"] = file;
                }
            }

            fake.Route(exchange.Request.Method, exchange.Request.Path, new Reply(exchange.Reply.Status, body));
        }
    }
}
