using Weir.Core.Json;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// What the page is told about the tray's update work: whether a tray is there to answer, and what it is doing. A click is
/// shown as under way only while there is a tray and the flag is recent, and a step a dead tray said it was at is not shown.
/// </summary>
public sealed class UpdateFilesTests : IDisposable
{
    private readonly StoreFixture _store = new(("WEIR_RUNTIME", "windows"));

    public void Dispose() => _store.Dispose();

    private string Home => _store.Options.WeirHome;

    private UpdateFiles Files() => new(_store.Options, _store.Clock);

    private void Beat(TimeSpan ago) =>
        File.WriteAllText(Path.Join(Home, UpdateFiles.TrayHeartbeatFileName), $"{{\"at\": \"{(_store.Clock.GetUtcNow() - ago):O}\"}}");

    private void Flag(string name, TimeSpan ago)
    {
        var path = Path.Join(Home, name);
        File.WriteAllText(path, string.Empty);
        File.SetLastWriteTimeUtc(path, (_store.Clock.GetUtcNow() - ago).UtcDateTime);
    }

    private void TrayWrites(string json) => File.WriteAllText(Path.Join(Home, UpdateFiles.StateFileName), json);

    private (string State, bool Running, string? Version) Read()
    {
        var state = Files().ReadState();
        return (((WireString)state["state"]).Value, state["tray_running"].IsTruthy, (state["pending_version"] as WireString)?.Value);
    }

    [Fact]
    public void A_tray_that_said_it_is_alive_lately_is_running()
    {
        Beat(TimeSpan.FromSeconds(10));

        Assert.True(Files().TrayIsRunning());
    }

    [Fact]
    public void A_tray_is_running_until_its_heartbeat_is_older_than_three_beats()
    {
        Beat(TimeSpan.Zero);
        _store.Clock.Advance(UpdateFiles.TrayHeartbeatMaxAge);
        Assert.True(Files().TrayIsRunning());

        _store.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(Files().TrayIsRunning());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("{\"at\": \"yesterday-ish\"}")]
    [InlineData("{\"when\": \"2026-01-15T10:00:00Z\"}")]
    public void A_heartbeat_that_is_missing_or_unreadable_is_no_tray(string? text)
    {
        if (text is not null)
        {
            File.WriteAllText(Path.Join(Home, UpdateFiles.TrayHeartbeatFileName), text);
        }

        Assert.False(Files().TrayIsRunning());
    }

    [Fact]
    public void An_install_that_is_not_the_windows_one_has_no_tray_however_fresh_a_file_is()
    {
        using var source = new StoreFixture();
        File.WriteAllText(Path.Join(source.Options.WeirHome, UpdateFiles.TrayHeartbeatFileName), $"{{\"at\": \"{source.Clock.GetUtcNow():O}\"}}");

        Assert.False(new UpdateFiles(source.Options, source.Clock).TrayIsRunning());
    }

    [Fact]
    public void A_click_the_tray_has_not_taken_up_yet_reads_as_under_way()
    {
        Beat(TimeSpan.Zero);
        Flag(UpdateFiles.DownloadFlagFileName, TimeSpan.FromSeconds(1));
        Assert.Equal(("downloading", true, null), Read());

        Flag(UpdateFiles.CheckFlagFileName, TimeSpan.FromSeconds(1));

        Assert.Equal(("checking", true, null), Read());
    }

    [Fact]
    public void A_flag_older_than_two_minutes_is_nobody_s_click()
    {
        Beat(TimeSpan.Zero);
        Flag(UpdateFiles.CheckFlagFileName, UpdateFiles.RequestMaxAge);
        Assert.Equal(("checking", true, null), Read());

        _store.Clock.Advance(TimeSpan.FromSeconds(1));
        Beat(TimeSpan.Zero);

        Assert.Equal(("idle", true, null), Read());
    }

    [Fact]
    public void A_flag_with_no_tray_to_take_it_does_not_read_as_under_way()
    {
        Flag(UpdateFiles.CheckFlagFileName, TimeSpan.Zero);
        Flag(UpdateFiles.DownloadFlagFileName, TimeSpan.Zero);

        Assert.Equal(("idle", false, null), Read());
    }

    [Theory]
    [InlineData("checking")]
    [InlineData("downloading")]
    public void A_step_the_tray_is_at_is_shown_while_it_is_alive_and_forgotten_once_it_is_not(string step)
    {
        TrayWrites($"{{\"state\": \"{step}\", \"downloaded\": false, \"version\": \"9.9.9\"}}");
        Beat(TimeSpan.Zero);
        Assert.Equal((step, true, "9.9.9"), Read());

        _store.Clock.Advance(UpdateFiles.TrayHeartbeatMaxAge + TimeSpan.FromSeconds(1));

        Assert.Equal(("idle", false, "9.9.9"), Read());
    }

    [Theory]
    [InlineData("downloaded", true)]
    [InlineData("failed", false)]
    public void What_a_dead_tray_left_behind_that_is_still_true_is_kept(string step, bool downloaded)
    {
        TrayWrites($"{{\"state\": \"{step}\", \"downloaded\": {(downloaded ? "true" : "false")}, \"version\": \"9.9.9\", \"failure\": \"No route.\"}}");

        Assert.Equal((step, false, "9.9.9"), Read());
    }

    [Fact]
    public void A_heartbeat_that_is_refused_for_a_moment_is_the_last_one_seen_and_keeps_ageing()
    {
        Beat(TimeSpan.Zero);
        var files = Files();
        Assert.True(files.TrayIsRunning());

        // The tray is renaming a new heartbeat over the old one: Windows refuses the open meanwhile.
        using (new FileStream(Path.Join(Home, UpdateFiles.TrayHeartbeatFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(files.TrayIsRunning());

            _store.Clock.Advance(UpdateFiles.TrayHeartbeatMaxAge + TimeSpan.FromSeconds(1));
            Assert.False(files.TrayIsRunning());
        }
    }

    [Fact]
    public void A_heartbeat_that_is_refused_before_any_was_seen_is_no_tray_yet()
    {
        Beat(TimeSpan.Zero);

        using (new FileStream(Path.Join(Home, UpdateFiles.TrayHeartbeatFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(Files().TrayIsRunning());
        }

        Assert.True(Files().TrayIsRunning());
    }

    [Fact]
    public void A_state_file_that_is_refused_for_a_moment_keeps_a_downloaded_update_downloaded()
    {
        TrayWrites("{\"state\": \"downloaded\", \"downloaded\": true, \"version\": \"9.9.9\"}");
        var files = Files();
        Assert.True(files.HasDownloadedUpdate());

        using (new FileStream(Path.Join(Home, UpdateFiles.StateFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(files.HasDownloadedUpdate());
            Assert.Equal("9.9.9", ((WireString)files.ReadState()["pending_version"]).Value);
        }
    }

    [Fact]
    public void A_state_file_that_is_not_there_is_the_default_state_even_after_one_was_read()
    {
        TrayWrites("{\"state\": \"downloaded\", \"downloaded\": true, \"version\": \"9.9.9\"}");
        var files = Files();
        Assert.True(files.HasDownloadedUpdate());

        File.Delete(Path.Join(Home, UpdateFiles.StateFileName));

        Assert.False(files.HasDownloadedUpdate());
    }
}
