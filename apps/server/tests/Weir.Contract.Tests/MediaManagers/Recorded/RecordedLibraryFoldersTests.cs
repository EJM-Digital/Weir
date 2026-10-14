using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using static Weir.Contract.Tests.MediaManagers.DelunoSyncRig;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Deluno's recording of the library folders Weir publishes, replayed against a Weir whose workflows were set up from a Deluno the way
/// a fresh install does: each carries Deluno's own id for its library, the folders it owns, and the minimum size a workflow starts with.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedLibraryFoldersTests
{
    private const string TvKey = "01a127b7c0eb7b108e7ec900f36acb11";

    [Fact]
    public async Task The_library_folders_of_workflows_set_up_from_Deluno_are_what_Deluno_recorded() =>
        await ReplayAsync(Recording.Load("library-folders"), RecordedWorld.MoviesKey, TvKey);

    [Fact]
    public async Task The_library_folders_Deluno_asked_for_in_its_own_session_are_what_Deluno_recorded() =>
        await ReplayAsync(DelunoSide.Request("library-folders-ask"), DelunoSide.MoviesKey, DelunoSide.TvKey);

    private static async Task ReplayAsync(Recording recording, string moviesKey, string tvKey)
    {
        using var rig = new TemporaryFolder();
        var movies = Native(rig, "Completed/Movies");
        var tv = Native(rig, "Completed/TV");
        using var deluno = FakeManager.StartDeluno(
        [
            Library(moviesKey, "Movies", "movie", MoviesReady(rig)),
            Library(tvKey, "TV Shows", "tv", TvReady(rig)),
        ]);
        deluno.Route("GET", DestinationsPath, new JsonObject
        {
            ["libraries"] = new JsonArray(
                Published(moviesKey, "Movies", Directory.CreateDirectory(movies).FullName, MoviesReady(rig)),
                Published(tvKey, "TV Shows", Directory.CreateDirectory(tv).FullName, TvReady(rig))),
        });
        await using var server = await WeirServer.StartNewAsync(new Dictionary<string, string>(SyncOn.Concat(Handoffs.SecretEnvironment)));
        using var admin = await server.CreateAdminClientAsync();
        await ConnectAsync(admin, deluno);
        await SyncedAsync(admin);
        var translation = new Translation()
            .Folder(RecordedWorld.RecordedMoviesWatched, movies)
            .Folder(RecordedWorld.RecordedMoviesOutput, MoviesReady(rig))
            .Folder(RecordedWorld.RecordedTvWatched, tv)
            .Folder(RecordedWorld.RecordedTvOutput, TvReady(rig))
            .Folder(@"C:\ProgramData\Weir", server.Home);

        await new Replay(() => server.CreateClient(), Handoffs.SecretValue, translation).SendAsync(recording);
    }
}
