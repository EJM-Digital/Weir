using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>The menu's Pause or Resume, and Change port.</summary>
public sealed class PauseAndPortTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly List<(string Text, ToolTipIcon Icon, Action? Click)> _balloons = [];
    private readonly List<string> _events = [];
    private bool _paused;

    public void Dispose() => _home.Dispose();

    private PauseControl Pause(string? runtimeHome = null) => new(
        runtimeHome ?? _home.Path,
        new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)),
        () => _paused,
        (_, text, icon, click) => _balloons.Add((text, icon, click)),
        () => _events.Add("log shown"));

    private bool RequestedPause()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home.Path, "pause-request.json")));
        return document.RootElement.GetProperty("paused").GetBoolean();
    }

    [Fact]
    public void Choosing_Pause_while_running_asks_the_server_to_pause()
    {
        Pause().Toggle();

        Assert.True(RequestedPause());
        Assert.Empty(_balloons);
    }

    [Fact]
    public void Choosing_Resume_while_paused_asks_the_server_to_resume()
    {
        _paused = true;

        Pause().Toggle();

        Assert.False(RequestedPause());
    }

    [Fact]
    public void When_the_request_cannot_be_written_the_person_is_told_and_a_click_shows_the_log()
    {
        var blocked = Path.Combine(_home.Path, "a-file");
        File.WriteAllText(blocked, "x");

        Pause(runtimeHome: blocked).Toggle();

        var balloon = Assert.Single(_balloons);
        Assert.Equal("Weir couldn't pause processing. See tray-host.log in the data folder.", balloon.Text);
        Assert.Equal(ToolTipIcon.Warning, balloon.Icon);
        balloon.Click!();
        Assert.Equal(["log shown"], _events);
    }

    [Fact]
    public void When_the_resume_request_cannot_be_written_the_person_is_told_that_resuming_failed()
    {
        _paused = true;
        var blocked = Path.Combine(_home.Path, "a-file");
        File.WriteAllText(blocked, "x");

        Pause(runtimeHome: blocked).Toggle();

        var balloon = Assert.Single(_balloons);
        Assert.Equal("Weir couldn't resume processing. See tray-host.log in the data folder.", balloon.Text);
        Assert.Contains("Could not ask the server to resume:", File.ReadAllText(Path.Combine(_home.Path, "tray-host.log")), StringComparison.Ordinal);
    }

    private PortChange Change(int? chosen, Func<int, Task<PortMove>>? move = null) => new(
        () => 9347,
        prompt =>
        {
            _events.Add($"asked from {prompt.CurrentPort}");
            return chosen;
        },
        async (port, _) =>
        {
            _events.Add($"moving to {port}, menu says {_currentMoving}");
            return await (move?.Invoke(port) ?? Task.FromResult(PortMove.Moved));
        },
        (_, text, icon, click) => _balloons.Add((text, icon, click)),
        () => _events.Add("opened Weir"),
        () => _events.Add("log shown"),
        () => _currentMoving = _portChange?.MovingTo,
        CancellationToken.None);

    private PortChange? _portChange;
    private int? _currentMoving;

    private async Task RunAsync(int? chosen, Func<int, Task<PortMove>>? move = null)
    {
        _portChange = Change(chosen, move);
        await _portChange.RunAsync();
    }

    [Fact]
    public async Task A_moved_port_offers_the_new_address_and_opens_nothing_by_itself()
    {
        await RunAsync(9400);

        var balloon = Assert.Single(_balloons);
        Assert.Equal("Weir is now at http://localhost:9400. Click to open it.", balloon.Text);
        Assert.DoesNotContain("opened Weir", _events);
        balloon.Click!();
        Assert.Contains("opened Weir", _events);
    }

    [Fact]
    public async Task The_menu_says_where_it_is_moving_to_while_it_moves_and_stops_saying_so_after()
    {
        await RunAsync(9400);

        Assert.Contains("moving to 9400, menu says 9400", _events);
        Assert.Null(_portChange!.MovingTo);
        Assert.Null(_currentMoving);
    }

    [Fact]
    public async Task A_move_that_fails_says_which_port_it_is_still_on_and_a_click_shows_the_log()
    {
        await RunAsync(9400, _ => Task.FromResult(PortMove.KeptOldPort));

        var balloon = Assert.Single(_balloons);
        Assert.Equal("Weir could not start on port 9400, so it is still at port 9347. See tray-host.log in the data folder.", balloon.Text);
        Assert.Equal(ToolTipIcon.Warning, balloon.Icon);
        balloon.Click!();
        Assert.Contains("log shown", _events);
        Assert.DoesNotContain("opened Weir", _events);
    }

    [Fact]
    public async Task A_move_that_fails_with_the_old_port_lost_as_well_says_Weir_has_stopped_and_a_click_shows_the_log()
    {
        await RunAsync(9400, _ => Task.FromResult(PortMove.Stopped));

        var balloon = Assert.Single(_balloons);
        Assert.Equal("Weir could not start on port 9400 or go back to port 9347, so it has stopped. Choose Restart Weir once the port is free. See tray-host.log in the data folder.", balloon.Text);
        Assert.Equal(ToolTipIcon.Warning, balloon.Icon);
        Assert.DoesNotContain("still at port", balloon.Text, StringComparison.Ordinal);
        balloon.Click!();
        Assert.Contains("log shown", _events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(9347)]
    public async Task Closing_the_question_or_choosing_the_same_port_moves_nothing(int? chosen)
    {
        await RunAsync(chosen);

        Assert.Empty(_balloons);
        Assert.DoesNotContain(_events, e => e.StartsWith("moving", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_move_cut_short_by_quitting_shows_nothing()
    {
        await RunAsync(9400, _ => throw new OperationCanceledException());

        Assert.Empty(_balloons);
        Assert.Null(_portChange!.MovingTo);
    }
}
