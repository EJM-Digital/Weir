using Weir.Contract.Tests.Harness;
using static Weir.Contract.Tests.MediaManagers.DelunoSyncRig;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// The workflow sync against what Deluno itself recorded: its answer to "where do downloads land" (rc.30.32, from Deluno's server code) and
/// the manifest a real Deluno answered in Compose (rc.30.35). A workflow is set up from the first, follows it when Deluno puts a library's
/// folders somewhere else, and is reported and left alone when the manifest stops naming its library. The manifest's libraries for the
/// first are listed by the test, as the first recording holds only the destinations.
/// </summary>
[ContractArea("media_managers")]
public sealed class RecordedWorkflowSyncTests
{
    private const string Destinations = "reply-download-destinations-libraries";

    private static string Slashed(string path) => path.Replace('\\', '/');

    private static string DownloadsOf(TemporaryFolder folder) => Directory.CreateDirectory(Path.Join(folder.Path, "films-downloads")).FullName;

    private static string ReadyOf(TemporaryFolder folder) => Directory.CreateDirectory(Path.Join(folder.Path, "films-ready")).FullName;

    [Fact]
    public async Task A_downloads_folder_Deluno_recorded_in_a_new_place_reaches_the_workflow_after_a_connection_test()
    {
        using var first = new TemporaryFolder();
        using var second = new TemporaryFolder();
        var placeholders = new DelunoPlaceholders(first.Path);
        using var deluno = DelunoRepliesTests.RigFake(placeholders, first.Path);
        deluno.Serve(DelunoSide.Reply(Destinations), placeholders, exchanges: [0]);
        var firstDownloads = DownloadsOf(first);
        ReadyOf(first);
        await using var server = await WeirServer.StartNewAsync(SyncOn);
        using var admin = await server.CreateAdminClientAsync();
        var connection = await ConnectAsync(admin, deluno);
        var movies = Movies(await SyncedAsync(admin, expected: 1));
        var id = (long)movies["id"]!;
        Assert.Equal(Slashed(firstDownloads), Slashed((string)movies["watched_folder"]!));

        var moved = placeholders.MovedTo(second.Path);
        var movedDownloads = DownloadsOf(second);
        var movedReady = ReadyOf(second);
        deluno.Libraries.Replace(DelunoRepliesTests.RigLibraries(moved, second.Path));
        deluno.Serve(DelunoSide.Reply(Destinations), moved, exchanges: [0]);
        var followed = await SyncUntilAsync(
            admin,
            connection,
            async () => Slashed((string)(await WorkflowAsync(admin, id))["watched_folder"]!) == Slashed(movedDownloads) ? await WorkflowAsync(admin, id) : null,
            "the workflow to follow the folder Deluno recorded");

        Assert.Equal(Slashed(movedReady), Slashed((string)followed["output_folder"]!));
        Assert.Equal((long)connection["id"]!, (long)followed["folders_synced_from_connection_id"]!);
    }

    [Fact]
    public async Task A_library_the_manifest_of_a_real_Deluno_does_not_name_is_reported_and_its_workflow_is_left_as_it_is()
    {
        using var folder = new TemporaryFolder();
        var placeholders = new DelunoPlaceholders(folder.Path);
        using var deluno = DelunoRepliesTests.RigFake(placeholders, folder.Path);
        deluno.Serve(DelunoSide.Reply(Destinations), placeholders, exchanges: [0]);
        DownloadsOf(folder);
        ReadyOf(folder);
        await using var server = await WeirServer.StartNewAsync(SyncOn);
        using var admin = await server.CreateAdminClientAsync();
        var connection = await ConnectAsync(admin, deluno);
        var movies = Movies(await SyncedAsync(admin, expected: 1));
        var id = (long)movies["id"]!;
        var before = Held(await WorkflowAsync(admin, id));
        var name = (string)movies["name"]!;

        // The real Deluno lists one library, its own Movies, and hands it to no processor: the library the workflow came from is not among them.
        var realManifest = RealStackSide.Only(RealStackSide.Load("real-connection"), exchange => exchange.Request.Path == Manifest);
        deluno.Route("GET", Manifest, _ => DelunoSide.AnswerOf(realManifest.Reply));
        await TestConnectionAsync(admin, connection);

        var notice = await NoticeAsync(admin, $"{(string)connection["name"]!} no longer has the library for {name}");
        var detail = notice["detail"]!.ToString();
        Assert.Contains($"Weir left {name} as it is.", detail, StringComparison.Ordinal);
        Assert.Contains("Remove this workflow if the library is gone for good, or unlink it from", detail, StringComparison.Ordinal);
        Assert.Equal(before, Held(await WorkflowAsync(admin, id)));
        Assert.Single((await ProcessingLibraries.ListAsync(admin)), row => row!["discovered_library_key"] is not null);
    }
}
