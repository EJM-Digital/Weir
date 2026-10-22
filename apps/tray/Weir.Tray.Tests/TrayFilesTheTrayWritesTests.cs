using System.Text.Json.Nodes;
using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The files the tray writes for the server (<c>pause-request.json</c>, <c>update-backup-request.json</c>, <c>update-state.json</c>,
/// <c>tray-heartbeat.json</c>, <c>lan-access</c>) are exactly the recorded files the server's own readers are tested on
/// (<c>apps/server/tests/fixtures/tray-files</c>).
/// </summary>
public sealed class TrayFilesTheTrayWritesTests : IDisposable
{
    private const string RecordedId = "0123456789abcdef0123456789abcdef";
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _home = TempDirectory.Create();
    private readonly DelayWatchingTimeProvider _clock = new();

    public TrayFilesTheTrayWritesTests() => _clock.SetUtcNow(RecordedTrayFiles.WrittenAt);

    public void Dispose() => _home.Dispose();

    private string Written(string fileName) => RecordedTrayFiles.Normalised(File.ReadAllText(Path.Combine(_home.Path, fileName)));

    private static string Recorded(string name) => RecordedTrayFiles.Normalised(RecordedTrayFiles.Text(name));

    [Fact]
    public void A_pause_request_is_written_as_recorded()
    {
        PauseRequestFile.Write(_home.Path, paused: true, RecordedTrayFiles.WrittenAt);

        Assert.Equal(Recorded("pause-request.json"), Written(PauseRequestFile.FileName));
    }

    [Fact]
    public void The_heartbeat_is_written_as_recorded()
    {
        TrayHeartbeatFile.Write(_home.Path, RecordedTrayFiles.WrittenAt, installed: true);

        Assert.Equal(Recorded("tray-heartbeat.json"), Written(TrayHeartbeatFile.FileName));
    }

    [Fact]
    public void The_state_of_an_update_is_written_as_recorded()
    {
        UpdateStateFile.Write(_home.Path, UpdatePhase.Downloaded, "9.9.9", checkedAt: RecordedTrayFiles.WrittenAt);

        Assert.Equal(Recorded("update-state.json"), Written(UpdateStateFile.FileName));
    }

    [Theory]
    [InlineData((int)ListenScope.OtherDevices, "lan-access-on.txt")]
    [InlineData((int)ListenScope.ThisPcOnly, "lan-access-off.txt")]
    public void The_choice_about_the_network_is_written_as_recorded(int scope, string recorded)
    {
        LanAccessSetting.Write(_home.Path, (ListenScope)scope);

        Assert.Equal(Recorded(recorded), Written(LanAccessSetting.FileName));
    }

    [Theory]
    [InlineData("lan-access-on.txt", (int)ListenScope.OtherDevices)]
    [InlineData("lan-access-off.txt", (int)ListenScope.ThisPcOnly)]
    public void The_choice_about_the_network_the_server_saved_is_read_as_saved(string recorded, int expected)
    {
        RecordedTrayFiles.PlaceIn(_home.Path, recorded, LanAccessSetting.FileName);

        Assert.Equal((ListenScope)expected, LanAccessSetting.Read(_home.Path, _ => { }));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_request_for_a_copy_of_the_data_is_written_as_recorded_and_the_servers_answer_is_understood()
    {
        RecordedTrayFiles.PlaceIn(_home.Path, "update-backup-ready");
        var server = UpdateBackupReady.Read(_home.Path);
        var request = new UpdateBackupRequest(_home.Path, _clock, () => server);
        using var stop = new CancellationTokenSource();

        var asking = request.AskAsync("1.0.0-rc.13", stop.Token);
        await _clock.NextDelay().WaitAsync(Ceiling);

        var written = Written(UpdateBackupRequest.RequestFileName);
        var id = (string)JsonNode.Parse(written)!["id"]!;
        Assert.Equal(Recorded("update-backup-request.json"), written.Replace(id, RecordedId, StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(_home.Path, UpdateBackupRequest.ResultFileName), RecordedTrayFiles.Text("update-backup-result-saved.json").Replace(RecordedId, id, StringComparison.Ordinal));
        _clock.Advance(UpdateBackupRequest.PollInterval);

        Assert.Equal(new UpdateBackupAnswer(UpdateBackupOutcome.Saved, null), await asking.WaitAsync(Ceiling));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_copy_that_could_not_be_saved_is_understood_with_the_reason_the_server_gave()
    {
        RecordedTrayFiles.PlaceIn(_home.Path, "update-backup-ready");
        var server = UpdateBackupReady.Read(_home.Path);
        var request = new UpdateBackupRequest(_home.Path, _clock, () => server);
        using var stop = new CancellationTokenSource();

        var asking = request.AskAsync("1.0.0-rc.13", stop.Token);
        await _clock.NextDelay().WaitAsync(Ceiling);
        var id = (string)JsonNode.Parse(Written(UpdateBackupRequest.RequestFileName))!["id"]!;
        File.WriteAllText(Path.Combine(_home.Path, UpdateBackupRequest.ResultFileName), RecordedTrayFiles.Text("update-backup-result-failed.json").Replace(RecordedId, id, StringComparison.Ordinal));
        _clock.Advance(UpdateBackupRequest.PollInterval);

        Assert.Equal(
            new UpdateBackupAnswer(UpdateBackupOutcome.Failed, "Something unexpected went wrong. The details are in Weir's log files."),
            await asking.WaitAsync(Ceiling));
    }
}
