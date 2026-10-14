using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The state Deluno's recording was made in, rebuilt on a test server that really works files: a Movies and a TV Shows workflow
/// that a fake Deluno manages, folders on this machine standing for the folders the recording names, and a replayer that maps the
/// recorded Windows paths onto them.
/// </summary>
internal sealed class RecordedWorld : IAsyncDisposable
{
    /// <summary>Deluno's id for its Movies library, as the recorded hand-offs name it.</summary>
    public const string MoviesKey = "01a127b7c0c9718d90fc38b21d1b9daf";

    public const string RecordedMoviesWatched = @"C:\Downloads\Completed\Movies";
    public const string RecordedMoviesOutput = @"C:\Weir\Ready\Movies";
    public const string RecordedTvWatched = @"C:\Downloads\Completed\TV";
    public const string RecordedTvOutput = @"C:\Weir\Ready\TV";
    public const string RecordedElsewhere = @"C:\golden\recorded\weir";

    private RecordedWorld(Scenario scenario, FakeManager deluno, JsonObject movies, Replay replay)
    {
        Scenario = scenario;
        Deluno = deluno;
        Movies = movies;
        Replay = replay;
    }

    public Scenario Scenario { get; }

    /// <summary>The fake Deluno, which receives Weir's reports.</summary>
    public FakeManager Deluno { get; }

    public JsonObject Movies { get; }

    public Replay Replay { get; }

    public Translation Translation => Replay.Translation;

    /// <summary>The film at the recorded path (this machine's folder for it), written for Weir to find; <paramref name="needsWork"/> gives it a track Weir cleans out.</summary>
    public string PutFilm(string recordedPath, bool needsWork = false)
    {
        var path = Translation.Apply(recordedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, needsWork ? FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])) : FilmNeedingNoChange());
        return path;
    }

    /// <summary>
    /// Makes every film's pass wait inside the fake ffmpeg until the file this returns is written, so a hand-off can be seen
    /// while Weir is working it. A film with nothing to clean is never held, so use <c>needsWork</c> films.
    /// </summary>
    public string HoldEveryPass()
    {
        var release = Path.Combine(Scenario.Root, "release-remux");
        Scenario.FakeTools.SetFileRule("*.mkv", new FileRule { Probe = FakeMedia.Probe(audioLanguages: ["eng", "fre"]), RemuxReleaseFile = release });
        return release;
    }

    public async Task WaitUntilWorkingAsync(string handoffId) =>
        await Poll.UntilAsync(
            async () => await RemuxJobs.StatusForHandoffAsync(Scenario.Admin, handoffId) == "leased", $"Weir to start working {handoffId}");

    /// <summary>What the file's panel says became of the copy Weir handed back.</summary>
    public async Task<JsonObject> HandbackAsync(string relativePath) =>
        (await Scenario.FileRowAsync(Movies, relativePath) ?? throw new InvalidOperationException($"{relativePath} has no row"))["handback"]!.AsObject();

    /// <summary>The titles of Activity's lines of one kind, newest first.</summary>
    public async Task<List<string>> ActivityTitlesAsync(string eventType) =>
        [.. (await Scenario.ActivityAsync(eventType)).Select(entry => (string)entry["title"]!)];

    /// <summary>
    /// A film the saved rules leave as it is (one English track, already the default), so Weir checks it, finds nothing to change and
    /// hands the file back unchanged, as it did the real films the recordings were made with.
    /// </summary>
    public static byte[] FilmNeedingNoChange()
    {
        var probe = FakeMedia.Probe();
        probe["streams"]!.AsArray().Single(stream => (string?)stream!["codec_type"] == "audio")!["disposition"] = new JsonObject { ["default"] = 1 };
        return FakeMedia.Bytes(probe);
    }

    /// <summary>
    /// The world of the recordings. <paramref name="moviesKey"/> is Deluno's id for its Movies library, as the recorded hand-offs name it
    /// (it differs from one recording session to the next). <paramref name="delunoOutputRoot"/> is the folder Deluno says Weir hands its
    /// cleaned files back to, as Deluno's machine writes it; null means this machine's own output folder, which is how the rc.13
    /// recordings were replayed. A Windows path there makes Weir rebuild the paths it reports under it, as it does for a Deluno
    /// on another machine.
    /// </summary>
    public static async Task<RecordedWorld> StartAsync(string moviesKey = MoviesKey, string? delunoOutputRoot = null)
    {
        var scenario = await Scenario.StartAsync();
        try
        {
            var deluno = scenario.Own(FakeManager.StartDeluno());
            deluno.Libraries.Add(new JsonObject
            {
                ["id"] = moviesKey,
                ["name"] = "Movies",
                ["mediaType"] = "movies",
                ["rootPath"] = "/deluno/library/movies",
                ["importWorkflow"] = "refine-before-import",
                ["processorOutputPath"] = delunoOutputRoot ?? scenario.Folders.Output,
            });
            var connection = await scenario.CreateConnectionAsync(deluno);
            var movies = await SetUpAsync(scenario, "movie", scenario.Folders, connection["id"]!);
            var tv = Folders.Make(Path.Combine(scenario.Root, "tv"));
            await SetUpAsync(scenario, "tv", tv, connection["id"]!, name: "TV Shows");
            var translation = new Translation()
                .Folder(RecordedMoviesWatched, scenario.Folders.Watched)
                .Folder(RecordedMoviesOutput, scenario.Folders.Output)
                .Folder(RecordedTvWatched, tv.Watched)
                .Folder(RecordedTvOutput, tv.Output)
                .Folder(RecordedElsewhere, Directory.CreateDirectory(Path.Combine(scenario.Root, "elsewhere")).FullName);
            return new RecordedWorld(scenario, deluno, movies, new Replay(() => scenario.Server.CreateClient(), Scenario.WebhookSecret, translation));
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }
    }

    /// <summary>A fresh install already has a Movies and a TV Shows workflow; each is pointed at these folders and linked to Deluno.</summary>
    private static async Task<JsonObject> SetUpAsync(Scenario scenario, string mediaType, Folders folders, JsonNode connectionId, string? name = null)
    {
        var seeded = (await ProcessingLibraries.ListAsync(scenario.Admin)).Single(row => (string?)row!["media_type"] == mediaType)!.AsObject();
        return await scenario.UpdateLibraryAsync(
            seeded,
            ("name", name ?? (string)seeded["name"]!),
            ("watched_folder", folders.Watched),
            ("work_folder", folders.Work),
            ("output_folder", folders.Output),
            ("ready_after_seconds", 0),
            ("min_file_size_mb", 0),
            ("skip_access_tests", true),
            ("retry_backoff_seconds", 1),
            ("minimum_free_disk_space_mb", 0),
            ("manager_connection_ids", new JsonArray(connectionId.DeepClone())));
    }

    public ValueTask DisposeAsync() => Scenario.DisposeAsync();
}
