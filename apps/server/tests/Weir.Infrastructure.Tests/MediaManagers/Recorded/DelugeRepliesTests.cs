using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// Weir's Deluge client against what a real Deluge Web UI answered (<c>fixtures/recorded/deluge</c>, made by
/// <c>apps/server/tools/Weir.ClientRecorder</c>). Every reply is a recorded one; each test also holds that Weir asked for exactly what
/// the recording says it asked for.
/// </summary>
public sealed class DelugeRepliesTests
{
    private const string Kind = DownloadClientKinds.Deluge;

    [Fact]
    public async Task The_connection_test_logs_in_with_the_password_alone()
    {
        var recording = ArrRecording.Load(Kind, "test");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Right(Kind));

        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach Deluge (recorded).", detail);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task A_password_Deluge_does_not_know_is_a_login_answered_false_and_is_refused_in_plain_words()
    {
        var recording = ArrRecording.Load(Kind, "test-wrong-password");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Wrong(Kind));

        Assert.False((bool)recording.Only.Reply.Body!["result"]!);
        Assert.False(ok);
        Assert.Equal("Weir reached Deluge (recorded), but the password was refused. Check it and save it again.", detail);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task A_Deluge_as_it_starts_has_a_download_folder_and_no_labels_because_the_label_plugin_is_off()
    {
        var recording = ArrRecording.Load(Kind, "folders-stock");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        Assert.Equal("Unknown method", (string)recording.Call("label.get_labels").Reply.Body!["error"]!["message"]!);
        Assert.Equal("/downloads", folders.CompletedFolder);
        Assert.Empty(folders.CategoryFolders);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task A_folder_to_move_completed_downloads_to_that_is_switched_off_is_not_where_they_end_up()
    {
        var recording = ArrRecording.Load(Kind, "folders-move-completed-off");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        var config = recording.Call("core.get_config").Reply.Body!["result"]!;
        Assert.False((bool)config["move_completed"]!);
        Assert.Equal("/downloads/complete", (string)config["move_completed_path"]!);
        Assert.Equal("/downloads/incomplete", folders.CompletedFolder);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task Only_a_label_that_moves_completed_downloads_has_a_folder_of_its_own()
    {
        var recording = ArrRecording.Load(Kind, "folders");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        Assert.Equal(["idle-recorder", "plain-recorder", "tv-recorder"], recording.Call("label.get_labels").Reply.Body!["result"]!.AsArray().Select(name => (string)name!));
        Assert.Equal("/downloads/complete", folders.CompletedFolder);
        Assert.Equal([new DownloadClientCategoryFolder("tv-recorder", "/downloads/complete/tv")], folders.CategoryFolders);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task The_session_cookie_is_sent_back_as_its_name_and_value_alone()
    {
        var recording = ArrRecording.Load(Kind, "folders");
        using var replay = new ClientReplay(recording);

        await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        Assert.Contains("Expires=", recording.Exchanges[0].Reply.Headers["set-cookie"], StringComparison.Ordinal);
        Assert.All(replay.Sent.Skip(1), request => Assert.Equal("_session_id=<scrubbed>", request.Headers["cookie"]));
    }
}
