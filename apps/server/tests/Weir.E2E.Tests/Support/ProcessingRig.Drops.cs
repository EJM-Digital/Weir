using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.E2E.Tests.Support;

/// <summary>What a rig can do besides receive a hand-off: a file nobody handed over, a workflow linked to the manager, and what the manager was told.</summary>
public sealed partial class ProcessingRig
{
    private const string Libraries = $"{WeirClient.Api}/processing/libraries";
    private const string Connections = $"{WeirClient.Api}/media-managers/connections";

    /// <summary>
    /// A film in a release folder of the Movies workflow's watched folder that no manager handed over, as a download client leaves one for
    /// Weir's own look at the folder, with a metadata file beside it. Weir is asked to work on it; with <paramref name="failFirstPass"/> its first pass fails.
    /// </summary>
    public async Task DropFileAsync(string releaseFolder, string fileName, bool failFirstPass = false)
    {
        var folder = Path.Combine(Watched, releaseFolder);
        Directory.CreateDirectory(folder);
        var probe = FakeMedia.Probe(audioLanguages: ["eng", "fre"]);
        await File.WriteAllBytesAsync(Path.Combine(folder, fileName), FakeMedia.Bytes(probe));
        await File.WriteAllTextAsync(Path.Combine(folder, MetadataFile), "metadata");
        _tools.SetFileRule(
            fileName,
            failFirstPass
                ? new FileRule { Probe = probe, RemuxError = "Conversion failed: the fake ffmpeg was told to fail", RemuxFailTimes = 1 }
                : new FileRule { Probe = probe });
        var queued = await _admin.PostWithCsrfAsync(
            $"{WeirClient.Api}/processing/jobs/file-remux-pass/enqueue",
            new JsonObject { ["relative_media_path"] = $"{releaseFolder}/{fileName}", ["library_id"] = await MoviesWorkflowIdAsync() });
        Assert.True(queued.Status == HttpStatusCode.OK, queued.ToString());
    }

    /// <summary>The metadata file <see cref="DropFileAsync"/> leaves beside the film.</summary>
    public const string MetadataFile = "film.nfo";

    /// <summary>Whether the release folder <see cref="DropFileAsync"/> or a hand-off made is still in the watched folder.</summary>
    public bool WatchedReleaseExists(string releaseFolder) => Directory.Exists(Path.Combine(Watched, releaseFolder));

    /// <summary>Whether Weir has written the cleaned film into its output folder.</summary>
    public bool OutputFileExists(string releaseFolder, string fileName) => File.Exists(Path.Combine(_folders.Path, "output", releaseFolder, fileName));

    /// <summary>Links the Movies workflow to the Deluno connection, as choosing the manager in the workflow's editor does.</summary>
    public async Task LinkMoviesToDelunoAsync()
    {
        var connections = await _admin.GetAsync(Connections);
        Assert.True(connections.Status == HttpStatusCode.OK, connections.ToString());
        var connection = (long)Assert.Single(connections.Elements)!["id"]!;
        var path = $"{Libraries}/{await MoviesWorkflowIdAsync()}";
        var movies = await _admin.GetAsync(path);
        Assert.True(movies.Status == HttpStatusCode.OK, movies.ToString());
        var body = LibraryBodies.Unchanged(movies.Fields);
        body["manager_connection_ids"] = new JsonArray(connection);
        var saved = await _admin.PutWithCsrfAsync(path, body);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    /// <summary>What Weir calls the Deluno connection.</summary>
    public async Task<string> DelunoNameAsync()
    {
        var connections = await _admin.GetAsync(Connections);
        Assert.True(connections.Status == HttpStatusCode.OK, connections.ToString());
        return (string)Assert.Single(connections.Elements)!["name"]!;
    }

    /// <summary>How many reports Weir has sent Deluno about a hand-off so far.</summary>
    public int ReportsSentToDeluno => _deluno.Requests.Count(request => request.Method == "POST" && request.Path == CallbackPath);

    /// <summary>The id of the Movies workflow.</summary>
    public async Task<long> MoviesWorkflowIdAsync()
    {
        var listed = await _admin.GetAsync(Libraries);
        Assert.True(listed.Status == HttpStatusCode.OK, listed.ToString());
        return (long)listed.Elements.Select(row => row!.AsObject()).First(row => (string?)row["media_type"] == "movie")["id"]!;
    }
}
