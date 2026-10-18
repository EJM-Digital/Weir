using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// Weir's SABnzbd client against what a real SABnzbd answered (<c>fixtures/recorded/sabnzbd</c>, made by
/// <c>apps/server/tools/Weir.ClientRecorder</c>). Every reply is a recorded one; each test also holds that Weir asked for exactly what
/// the recording says it asked for.
/// </summary>
public sealed class SabnzbdRepliesTests
{
    private const string Kind = DownloadClientKinds.Sabnzbd;

    [Fact]
    public async Task The_connection_test_reads_the_misc_settings_with_the_api_key()
    {
        var recording = ArrRecording.Load(Kind, "test");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Right(Kind));

        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach SABnzbd (recorded).", detail);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task A_key_SABnzbd_does_not_know_is_403_and_is_refused_in_plain_words()
    {
        var recording = ArrRecording.Load(Kind, "test-wrong-key");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Wrong(Kind));

        Assert.Equal(403, recording.Only.Reply.Status);
        Assert.False(ok);
        Assert.Equal("Weir reached SABnzbd (recorded), but the API key was refused. Check the key and save it again.", detail);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task The_folders_are_the_completed_folder_and_the_categories_that_name_a_folder_of_their_own_in_the_order_SABnzbd_lists_them()
    {
        var recording = ArrRecording.Load(Kind, "folders");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        Assert.Equal("/downloads/complete", folders.CompletedFolder);
        Assert.Equal(
            [
                new DownloadClientCategoryFolder("tv-recorder", "/downloads/complete/tv"),
                new DownloadClientCategoryFolder("elsewhere-recorder", "/media/elsewhere"),
                new DownloadClientCategoryFolder("movies", "/downloads/complete"),
                new DownloadClientCategoryFolder("tv", "/downloads/complete"),
                new DownloadClientCategoryFolder("audio", "/downloads/complete"),
                new DownloadClientCategoryFolder("software", "/downloads/complete"),
            ],
            folders.CategoryFolders);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task A_key_SABnzbd_does_not_know_reads_no_folders()
    {
        var recording = ArrRecording.Load(Kind, "folders-wrong-key");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Wrong(Kind));

        Assert.All(recording.Exchanges, exchange => Assert.Equal(403, exchange.Reply.Status));
        Assert.Equal(DownloadClientFolders.Empty, folders);
        replay.AssertSentAsRecorded();
    }
}
