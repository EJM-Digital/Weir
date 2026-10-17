using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// Weir's Transmission client against what a real Transmission answered, with and without a sign-in
/// (<c>fixtures/recorded/transmission</c>, made by <c>apps/server/tools/Weir.ClientRecorder</c>). Every reply is a recorded one; each test
/// also holds that Weir asked for exactly what the recording says it asked for.
/// </summary>
public sealed class TransmissionRepliesTests
{
    private const string Kind = DownloadClientKinds.Transmission;

    [Fact]
    public async Task An_open_Transmission_answers_409_first_and_is_connected_once_the_session_id_is_sent_back()
    {
        var recording = ArrRecording.Load(Kind, "test-open");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Anonymous(Kind));

        Assert.Equal([409, 200], recording.Exchanges.Select(exchange => exchange.Reply.Status));
        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach Transmission (recorded).", detail);
        replay.AssertSentAsRecorded();
        Assert.False(replay.Sent[0].Headers.ContainsKey("authorization"));
        Assert.Equal(recording.Exchanges[0].Reply.Headers["x-transmission-session-id"], replay.Sent[1].Headers["x-transmission-session-id"]);
    }

    [Fact]
    public async Task A_sign_in_is_sent_on_both_calls_and_the_session_id_on_the_second()
    {
        var recording = ArrRecording.Load(Kind, "test");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Right(Kind));

        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach Transmission (recorded).", detail);
        replay.AssertSentAsRecorded();
        Assert.All(replay.Sent, request => Assert.StartsWith("Basic ", request.Headers["authorization"], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("test-wrong-password")]
    [InlineData("test-no-credentials")]
    public async Task A_refused_sign_in_is_401_and_says_so_in_plain_words(string state)
    {
        var recording = ArrRecording.Load(Kind, state);
        using var replay = new ClientReplay(recording);
        var connection = state == "test-wrong-password" ? ClientReplay.Wrong(Kind) : ClientReplay.Anonymous(Kind);

        var (ok, detail) = await replay.Port(Kind).TestAsync(connection);

        Assert.Equal(401, recording.Only.Reply.Status);
        Assert.False(ok);
        Assert.Equal("Weir reached Transmission (recorded), but the username or password was refused. Check them and save again.", detail);
        replay.AssertSentAsRecorded();
    }

    [Fact]
    public async Task The_folder_is_the_download_folder_and_Transmission_has_no_categories()
    {
        var recording = ArrRecording.Load(Kind, "folders");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        Assert.Equal("/downloads/complete", folders.CompletedFolder);
        Assert.Empty(folders.CategoryFolders);
        replay.AssertSentAsRecorded();
    }
}
