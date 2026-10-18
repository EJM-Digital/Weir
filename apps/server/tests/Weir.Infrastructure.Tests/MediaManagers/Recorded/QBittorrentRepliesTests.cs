using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// Weir's qBittorrent client against what a real qBittorrent answered, in every version recorded
/// (<c>fixtures/recorded/qbittorrent</c>, made by <c>apps/server/tools/Weir.ClientRecorder</c>). Every reply is a recorded one; each test
/// also holds that Weir asked for exactly what the recording says it asked for.
/// </summary>
public sealed class QBittorrentRepliesTests
{
    private const string Kind = DownloadClientKinds.QBittorrent;

    public static TheoryData<string> Versions => [.. ArrRecording.Versions(Kind)];

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task The_connection_test_logs_in_and_reads_the_preferences(string version)
    {
        var recording = ArrRecording.Load(Kind, version, "test");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Right(Kind));

        Assert.Equal(200, recording.Exchanges[0].Reply.Status);
        Assert.Equal("Ok.", recording.Exchanges[0].Reply.Text);
        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach qBittorrent (recorded).", detail);
        replay.AssertSentAsRecorded();
        Assert.Equal(recording.Exchanges[0].Reply.Headers["set-cookie"].Split(';')[0], replay.Sent[1].Headers["cookie"]);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_password_qBittorrent_does_not_know_is_refused_in_plain_words(string version)
    {
        var recording = ArrRecording.Load(Kind, version, "test-wrong-password");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Wrong(Kind));

        Assert.Equal(200, recording.Only.Reply.Status);
        Assert.Equal("Fails.", recording.Only.Reply.Text);
        Assert.False(ok);
        Assert.Equal("Weir reached qBittorrent (recorded), but the username or password was refused. Check them and save again.", detail);
        replay.AssertSentAsRecorded();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task The_folders_are_the_default_save_path_and_the_categories_that_have_one_of_their_own(string version)
    {
        var recording = ArrRecording.Load(Kind, version, "folders");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        Assert.Equal("/downloads/complete", folders.CompletedFolder);
        Assert.Equal(
            [new DownloadClientCategoryFolder("elsewhere-recorder", "/media/elsewhere"), new DownloadClientCategoryFolder("tv-recorder", "/downloads/complete/tv")],
            folders.CategoryFolders);
        Assert.Equal(3, recording.Exchanges.Count);
        replay.AssertSentAsRecorded();
    }
}
