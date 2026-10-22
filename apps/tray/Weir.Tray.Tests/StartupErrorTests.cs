using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// A server that cannot start says why in startup-error.txt, and the tray shows that in its hover text and balloon instead of a
/// bare "couldn't start"; how far a start has got is startup-progress.json (StartWatchTests, ServerHostTests).
/// </summary>
public sealed class StartupErrorTests : IDisposable
{
    private const string Headline = "Couldn't save a copy of its data before updating";
    private const string Sentence =
        "Weir couldn't save a copy of its data before updating, so it didn't change anything: Drive C: needs about 480 MB free to hold the copy and has 120 MB. Free up space there, then try again.";

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    // Well before the notes the tests write: the file system stamps files with a coarser clock than DateTime.UtcNow reads.
    private readonly DateTime _serverStarted = DateTime.UtcNow - TimeSpan.FromMinutes(1);

    public void Dispose() => _home.Dispose();

    private string ErrorPath => Path.Combine(_home.Path, StartupNotes.ErrorFileName);

    [Fact]
    public void The_reason_is_a_headline_then_the_whole_sentence()
    {
        File.WriteAllText(ErrorPath, $"{Headline}\n{Sentence}");

        Assert.Equal(new StartupError(Headline, Sentence), StartupNotes.ReadError(_home.Path, _serverStarted));
    }

    [Fact]
    public void A_file_with_only_a_headline_uses_it_for_the_balloon_too()
    {
        File.WriteAllText(ErrorPath, Headline);

        Assert.Equal(new StartupError(Headline, Headline), StartupNotes.ReadError(_home.Path, _serverStarted));
    }

    [Fact]
    public void A_headline_followed_by_an_empty_line_uses_the_headline_for_the_balloon_too()
    {
        File.WriteAllText(ErrorPath, $"{Headline}\n\n");

        Assert.Equal(new StartupError(Headline, Headline), StartupNotes.ReadError(_home.Path, _serverStarted));
    }

    [Fact]
    public void A_note_is_read_whenever_it_was_written_for_a_server_whose_start_is_not_known()
    {
        File.WriteAllText(ErrorPath, $"{Headline}\n{Sentence}");
        File.SetLastWriteTimeUtc(ErrorPath, _serverStarted - TimeSpan.FromDays(1));

        Assert.Equal(new StartupError(Headline, Sentence), StartupNotes.ReadError(_home.Path, serverStartedUtc: null));
    }

    [Fact]
    public void A_reason_the_last_server_left_is_not_this_ones()
    {
        File.WriteAllText(ErrorPath, $"{Headline}\n{Sentence}");
        File.SetLastWriteTimeUtc(ErrorPath, _serverStarted - TimeSpan.FromMinutes(10));

        Assert.Null(StartupNotes.ReadError(_home.Path, _serverStarted));
    }

    [Fact]
    public void No_file_and_an_empty_file_are_no_reason()
    {
        Assert.Null(StartupNotes.ReadError(_home.Path, _serverStarted));

        File.WriteAllText(ErrorPath, "  \n");

        Assert.Null(StartupNotes.ReadError(_home.Path, _serverStarted));
    }

    [Fact]
    public void A_note_written_the_instant_before_the_server_began_is_the_last_ones_not_this_ones()
    {
        File.WriteAllText(ErrorPath, $"{Headline}\n{Sentence}");
        File.SetLastWriteTimeUtc(ErrorPath, _serverStarted - TimeSpan.FromMilliseconds(1));

        Assert.Null(StartupNotes.ReadError(_home.Path, _serverStarted));

        File.SetLastWriteTimeUtc(ErrorPath, _serverStarted);

        Assert.NotNull(StartupNotes.ReadError(_home.Path, _serverStarted));
    }

    [Fact]
    public void The_hover_text_of_a_stopped_server_gives_the_reason_within_the_limit()
    {
        var state = new TrayState(ServerPhase.Stopped, null, null, 9347, StartupError: new StartupError(Headline, Sentence));

        Assert.Equal("Weir - Stopped - Couldn't save a copy of its data before updating - choose Restart Weir", state.HoverText);
        Assert.True(state.HoverText.Length <= TrayState.HoverLimit);
        Assert.Equal(TrayDot.Red, state.Dot);
    }

    [Fact]
    public void A_stopped_server_that_gave_no_reason_is_worded_as_before()
    {
        var state = new TrayState(ServerPhase.Stopped, null, null, 9347);

        Assert.Equal("Weir - Stopped - choose Restart Weir", state.HoverText);
    }

    [Fact]
    public void The_balloons_carry_the_reason_and_the_way_back()
    {
        var why = new StartupError(Headline, Sentence);

        Assert.Equal($"Weir couldn't start. {Sentence} Click to try again.", TrayBalloons.CouldNotStartBecause(why));
        Assert.Equal($"Weir stopped. {Sentence} Click to try again.", TrayBalloons.StoppedBecause(why));
    }

    [Fact]
    public void The_balloons_for_a_supervision_that_gave_up_say_how_many_tries_and_where_the_details_are()
    {
        Assert.Equal(
            "Weir didn't start after 5 tries, so it has stopped trying. The details are in the tray log. Click to try again.",
            TrayBalloons.GaveUpText(5));
        Assert.Equal(
            "Weir stopped and didn't start again after 5 tries. The details are in the tray log. Click to try again.",
            TrayBalloons.StoppedAfterTriesText(5));
    }

    [Fact]
    public void One_try_reads_as_one_try_not_one_tries()
    {
        Assert.Equal("1 try", TrayBalloons.Tries(1));
        Assert.Equal("2 tries", TrayBalloons.Tries(2));
        Assert.Equal(
            "Weir didn't start after 1 try, so it has stopped trying. The details are in the tray log. Click to try again.",
            TrayBalloons.GaveUpText(1));
        Assert.Equal(
            "Weir stopped and didn't start again after 1 try. The details are in the tray log. Click to try again.",
            TrayBalloons.StoppedAfterTriesText(1));
    }

    [Fact]
    public void A_start_that_stopped_because_the_port_window_was_closed_says_so_and_not_how_many_tries()
    {
        Assert.Equal(
            "Weir didn't start because you closed the port window without choosing a port. Click to try again.",
            TrayBalloons.NoPortChosenText);
        Assert.DoesNotContain("tries", TrayBalloons.NoPortChosenText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_balloons_for_a_port_another_program_holds_name_the_port_and_say_what_to_do()
    {
        Assert.Equal(
            "Weir didn't start after 5 tries because another program is using port 8080. Close that program, or use Change port, then click to try again.",
            TrayBalloons.PortInUseText(8080, 5));
        Assert.Equal(
            "Weir stopped and didn't start again after 5 tries because another program is using port 8080. Close that program, or use Change port, then click to try again.",
            TrayBalloons.StoppedPortInUseText(8080, 5));
        Assert.StartsWith("Weir didn't start after 1 try because", TrayBalloons.PortInUseText(8080, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void The_hover_text_of_a_server_the_supervision_gave_up_on_says_so()
    {
        var state = new TrayState(ServerPhase.Stopped, null, null, 9347, StartupError: new StartupError("Didn't start after 5 tries", string.Empty));

        Assert.Equal("Weir - Stopped - Didn't start after 5 tries - choose Restart Weir", state.HoverText);
        Assert.Equal(TrayDot.Red, state.Dot);
    }
}
