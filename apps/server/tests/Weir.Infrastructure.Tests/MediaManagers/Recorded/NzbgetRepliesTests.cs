using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// Weir's NZBGet client against what a real NZBGet answered (<c>fixtures/recorded/nzbget</c>, made by
/// <c>apps/server/tools/Weir.ClientRecorder</c>). Every reply is a recorded one; each test also holds that Weir asked for exactly what
/// the recording says it asked for.
/// </summary>
public sealed class NzbgetRepliesTests
{
    private const string Kind = DownloadClientKinds.Nzbget;

    [Fact]
    public async Task The_connection_test_asks_for_the_config_with_a_basic_sign_in()
    {
        var recording = ArrRecording.Load(Kind, "test");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Right(Kind));

        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach NZBGet (recorded).", detail);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task A_password_NZBGet_does_not_know_is_401_and_is_refused_in_plain_words()
    {
        var recording = ArrRecording.Load(Kind, "test-wrong-password");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Wrong(Kind));

        Assert.Equal(401, recording.Only.Reply.Status);
        Assert.False(ok);
        Assert.Equal("Weir reached NZBGet (recorded), but the username or password was refused. Check them and save again.", detail);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task The_folders_are_the_destination_folder_and_the_categories_that_have_one_of_their_own()
    {
        var recording = ArrRecording.Load(Kind, "folders");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        Assert.Equal("/downloads/completed", folders.CompletedFolder);
        Assert.Equal([new DownloadClientCategoryFolder("Movies", "/downloads/complete/category-one")], folders.CategoryFolders);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task A_password_NZBGet_does_not_know_reads_no_folders()
    {
        var recording = ArrRecording.Load(Kind, "folders-wrong-password");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Wrong(Kind));

        Assert.Equal(401, recording.Only.Reply.Status);
        Assert.Equal(DownloadClientFolders.Empty, folders);
        replay.AssertSentAsRecorded();
    }
}
