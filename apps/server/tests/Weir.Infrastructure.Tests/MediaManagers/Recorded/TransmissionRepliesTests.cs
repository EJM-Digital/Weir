using Weir.Core.MediaManagers;
using Weir.Tests.Shared;

namespace Weir.Infrastructure.Tests.MediaManagers.Recorded;

/// <summary>
/// Weir's Transmission client against what a real Transmission answered, in every version recorded, with and without a sign-in
/// (<c>fixtures/recorded/transmission</c>, made by <c>apps/server/tools/Weir.ClientRecorder</c>). Every reply is a recorded one; each test
/// also holds that Weir asked for exactly what the recording says it asked for.
/// </summary>
public sealed class TransmissionRepliesTests
{
    private const string Kind = DownloadClientKinds.Transmission;

    public static TheoryData<string> Versions => [.. ArrRecording.Versions(Kind)];

    public static TheoryData<string, string> RefusedStates
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var version in ArrRecording.Versions(Kind))
            {
                data.Add(version, "test-wrong-password");
                data.Add(version, "test-nothing-saved");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task An_open_Transmission_answers_409_first_and_is_connected_once_the_session_id_is_sent_back(string version)
    {
        var recording = ArrRecording.Load(Kind, version, "test-open");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Anonymous(Kind));

        Assert.Equal([409, 200], recording.Exchanges.Select(exchange => exchange.Reply.Status));
        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach Transmission (recorded).", detail);
        replay.AssertSentAsRecorded();
        Assert.False(replay.Sent[0].Headers.ContainsKey("authorization"));
        Assert.Equal(recording.Exchanges[0].Reply.Headers["x-transmission-session-id"], replay.Sent[1].Headers["x-transmission-session-id"]);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_sign_in_is_sent_on_both_calls_and_the_session_id_on_the_second(string version)
    {
        var recording = ArrRecording.Load(Kind, version, "test");
        using var replay = new ClientReplay(recording);

        var (ok, detail) = await replay.Port(Kind).TestAsync(ClientReplay.Right(Kind));

        Assert.True(ok);
        Assert.Equal("Connected. Weir can reach Transmission (recorded).", detail);
        replay.AssertSentAsRecorded();
        Assert.All(replay.Sent, request => Assert.StartsWith("Basic ", request.Headers["authorization"], StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(RefusedStates))]
    public async Task A_refused_sign_in_is_401_and_says_so_in_plain_words(string version, string state)
    {
        var recording = ArrRecording.Load(Kind, version, state);
        using var replay = new ClientReplay(recording);
        var connection = state == "test-wrong-password" ? ClientReplay.Wrong(Kind) : ClientReplay.Anonymous(Kind);

        var (ok, detail) = await replay.Port(Kind).TestAsync(connection);

        Assert.Equal(401, recording.Only.Reply.Status);
        Assert.False(ok);
        Assert.Equal("Weir reached Transmission (recorded), but the username or password was refused. Check them and save again.", detail);
        replay.AssertSentAsRecorded();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task The_folder_is_the_download_folder_and_Transmission_has_no_categories(string version)
    {
        var recording = ArrRecording.Load(Kind, version, "folders");
        using var replay = new ClientReplay(recording);

        var folders = await replay.Port(Kind).ReadFoldersAsync(ClientReplay.Right(Kind));

        Assert.Equal("/downloads/complete", folders.CompletedFolder);
        Assert.Empty(folders.CategoryFolders);
        replay.AssertSentAsRecorded();
    }
}
