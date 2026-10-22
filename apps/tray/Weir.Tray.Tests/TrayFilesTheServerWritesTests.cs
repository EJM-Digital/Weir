using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// The files the server writes for the tray (<c>tray-status.json</c>, <c>work-state.json</c>, <c>update-backup-ready</c>,
/// <c>startup-error.txt</c>, <c>startup-progress.json</c>) are read by the tray's own readers exactly as the server wrote them
/// (<c>apps/server/tests/fixtures/tray-files</c>, which the server's tests check against its writers). The server's answer to a request
/// for a copy of the data is read in <see cref="TrayFilesTheTrayWritesTests"/>, where the request it answers is written.
/// </summary>
public sealed class TrayFilesTheServerWritesTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.Create();
    private readonly List<string> _log = [];

    public void Dispose() => _home.Dispose();

    [Fact]
    public void The_status_is_read_as_the_server_wrote_it()
    {
        RecordedTrayFiles.PlaceIn(_home.Path, "tray-status.json");

        var status = TrayStatusFile.Read(_home.Path, _log.Add).Status;

        Assert.NotNull(status);
        Assert.True(status.Paused);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 18, 30, 0, TimeSpan.Zero), status.PausedUntil);
        Assert.Equal(["Deluno"], status.ManagersUnreachable);
        Assert.Equal(["The watched folder for Movies"], status.FoldersUnreachable);
        Assert.True(status.ServerOk);
        Assert.Empty(_log);
    }

    [Fact]
    public void The_answer_to_is_Weir_idle_is_read_as_the_server_wrote_it_and_goes_stale()
    {
        RecordedTrayFiles.PlaceIn(_home.Path, "work-state.json");

        Assert.Equal(ServerWork.Idle, WorkStateFile.Read(_home.Path, RecordedTrayFiles.WrittenAt));
        Assert.Equal(ServerWork.Idle, WorkStateFile.Read(_home.Path, RecordedTrayFiles.WrittenAt + WorkStateFile.MaxAge));
        Assert.Equal(ServerWork.Unknown, WorkStateFile.Read(_home.Path, RecordedTrayFiles.WrittenAt + WorkStateFile.MaxAge + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_marker_that_says_the_server_can_save_a_copy_names_the_server()
    {
        RecordedTrayFiles.PlaceIn(_home.Path, "update-backup-ready");

        var server = UpdateBackupReady.Read(_home.Path);

        Assert.Equal(new RunningServer(4242, new DateTime(2026, 1, 15, 9, 0, 0, 123, DateTimeKind.Utc).AddTicks(4560)), server);
    }

    [Fact]
    public void The_reason_a_start_failed_is_read_as_a_headline_and_a_sentence()
    {
        RecordedTrayFiles.PlaceIn(_home.Path, "startup-error.txt");

        var error = StartupNotes.ReadError(_home.Path, serverStartedUtc: null);

        Assert.Equal(
            new StartupError(
                "Weir can't open its data",
                "Weir can't open its database because another program is using it. Close that program, then start Weir again."),
            error);
    }

    [Fact]
    public void How_far_a_start_has_got_is_read_from_the_file_and_from_the_line_on_standard_output()
    {
        RecordedTrayFiles.PlaceIn(_home.Path, "startup-progress.json");
        var expected = new StartProgress(4242, "migrating", Step: 3, Steps: 7, TimeSpan.FromSeconds(468), RecordedTrayFiles.WrittenAt);

        Assert.Equal(expected, StartProgressFile.Read(_home.Path));
        Assert.Equal(expected, StartProgressFile.ParseLine(StartProgressFile.Prefix + RecordedTrayFiles.Text("startup-progress.json")));
    }
}
