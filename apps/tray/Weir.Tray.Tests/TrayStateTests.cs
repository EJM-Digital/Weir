using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// What the icon shows and says (docs/tray-standard.md): a dot for the platform's health, a mark for a pause or an update, and the
/// status line and hover text that say what is wrong when the dot is amber or red.
/// </summary>
public sealed class TrayStateTests
{
    private static readonly TrayStatus AllWell = new(false, null, [], [], true);

    private static TrayState Running(TrayStatus? server = null, string? update = null) =>
        new(ServerPhase.Running, server ?? AllWell, update, 9347);

    private static TrayState Starting(TrayStatus? server = null, string? update = null) =>
        new(ServerPhase.Starting, server, update, 9347);

    private static TrayState Stopped(TrayStatus? server = null, string? update = null) =>
        new(ServerPhase.Stopped, server, update, 9347);

    [Fact]
    public void Running_with_everything_answering_is_green()
    {
        Assert.Equal(TrayDot.Green, Running().Dot);
    }

    [Fact]
    public void A_media_manager_that_does_not_answer_makes_the_dot_amber()
    {
        Assert.Equal(TrayDot.Amber, Running(AllWell with { ManagersUnreachable = ["Deluno"] }).Dot);
    }

    [Fact]
    public void A_folder_that_cannot_be_reached_makes_the_dot_amber()
    {
        Assert.Equal(TrayDot.Amber, Running(AllWell with { FoldersUnreachable = ["The output folder for Movies"] }).Dot);
    }

    [Fact]
    public void A_stopped_server_is_red()
    {
        Assert.Equal(TrayDot.Red, Stopped().Dot);
    }

    [Fact]
    public void A_server_that_is_finishing_its_jobs_to_stop_blinks_and_says_so()
    {
        var stopping = new TrayState(ServerPhase.Stopping, AllWell, null, 9347);

        Assert.Equal(TrayDot.Starting, stopping.Dot);
        Assert.Equal("Weir - Stopping...", stopping.HoverText);
    }

    [Fact]
    public void A_server_that_says_it_is_stopping_blinks_instead_of_flashing_red_while_it_is_started_again()
    {
        var stopping = AllWell with { ServerOk = false, ManagersUnreachable = ["Deluno"] };

        Assert.Equal(TrayDot.Starting, Running(stopping).Dot);
        Assert.Equal("Weir - Starting...", Running(stopping).HoverText);
    }

    [Fact]
    public void Red_is_for_a_server_that_is_stopped_for_good_whatever_it_last_said()
    {
        var everything = new TrayStatus(false, null, ["Deluno"], ["The watched folder for Movies"], ServerOk: false);

        Assert.Equal(TrayDot.Red, Stopped(everything).Dot);
        Assert.Equal(TrayDot.Red, Stopped(AllWell).Dot);
    }

    [Fact]
    public void A_running_server_with_no_readable_status_blinks_until_the_deadline_and_then_is_amber()
    {
        var waiting = new TrayState(ServerPhase.Running, null, null, 9347);
        var overdue = new TrayState(ServerPhase.Running, null, null, 9347, StatusOverdue: true);

        Assert.Equal(TrayDot.Starting, waiting.Dot);
        Assert.Equal("Weir - Starting...", waiting.HoverText);
        Assert.Equal(TrayDot.Amber, overdue.Dot);
        Assert.Equal("Weir - Can't read its status", overdue.HoverText);
    }

    [Fact]
    public void Being_overdue_changes_nothing_once_the_server_has_said_how_it_is_or_when_it_is_not_running()
    {
        Assert.Equal(TrayDot.Green, new TrayState(ServerPhase.Running, AllWell, null, 9347, StatusOverdue: true).Dot);
        Assert.Equal(TrayDot.Starting, new TrayState(ServerPhase.Starting, null, null, 9347, StatusOverdue: true).Dot);
        Assert.Equal(TrayDot.Red, new TrayState(ServerPhase.Stopped, null, null, 9347, StatusOverdue: true).Dot);
    }

    [Fact]
    public void The_dot_blinks_while_the_server_starts()
    {
        Assert.Equal(TrayDot.Starting, Starting().Dot);
    }

    [Fact]
    public void The_dot_goes_on_blinking_after_the_server_is_up_until_it_has_said_how_things_are()
    {
        Assert.Equal(TrayDot.Starting, new TrayState(ServerPhase.Running, null, null, 9347).Dot);
        Assert.Equal(TrayDot.Green, Running().Dot);
    }

    [Fact]
    public void A_pause_an_update_and_files_never_change_the_dot()
    {
        Assert.Equal(TrayDot.Green, Running(AllWell with { Paused = true }).Dot);
        Assert.Equal(TrayDot.Green, Running(update: "1.0.0-rc.12").Dot);
        Assert.Equal(TrayDot.Amber, Running(AllWell with { Paused = true, ManagersUnreachable = ["Deluno"] }, update: "1.0.0").Dot);
    }

    [Fact]
    public void What_a_server_wrote_before_it_stopped_or_restarted_is_not_shown()
    {
        var stale = new TrayStatus(Paused: true, null, ["Deluno"], ["The watched folder for Movies"], ServerOk: true);

        Assert.Equal(TrayDot.Starting, Starting(stale).Dot);
        Assert.Equal(TrayMark.None, Starting(stale).Mark);
        Assert.Equal("Starting...", Starting(stale).StatusLine);
        Assert.Equal(TrayDot.Red, Stopped(stale).Dot);
        Assert.Equal("Stopped - choose Restart Weir", Stopped(stale).StatusLine);
    }

    [Fact]
    public void There_is_no_mark_when_there_is_nothing_to_add()
    {
        Assert.Equal(TrayMark.None, Running().Mark);
        Assert.Equal(TrayMark.None, Running(AllWell with { ManagersUnreachable = ["Deluno"] }).Mark);
    }

    [Fact]
    public void Paused_shows_the_two_bars()
    {
        Assert.Equal(TrayMark.Paused, Running(AllWell with { Paused = true }).Mark);
    }

    [Fact]
    public void A_downloaded_update_shows_the_arrow_even_when_the_server_is_stopped()
    {
        Assert.Equal(TrayMark.UpdateReady, Running(update: "1.0.0-rc.12").Mark);
        Assert.Equal(TrayMark.UpdateReady, Stopped(update: "1.0.0-rc.12").Mark);
    }

    [Fact]
    public void The_pause_bars_win_over_the_update_arrow()
    {
        Assert.Equal(TrayMark.Paused, Running(AllWell with { Paused = true }, update: "1.0.0").Mark);
    }

    [Fact]
    public void A_blinking_dot_is_drawn_only_while_lit_and_a_known_dot_always_is()
    {
        Assert.Equal(new TrayIconKey(TrayDot.Starting, TrayMark.None), Starting().IconKey(blinkLit: true));
        Assert.Equal(new TrayIconKey(null, TrayMark.None), Starting().IconKey(blinkLit: false));
        Assert.Equal(new TrayIconKey(null, TrayMark.UpdateReady), Starting(update: "1.0.0").IconKey(blinkLit: false));
        Assert.Equal(new TrayIconKey(TrayDot.Green, TrayMark.Paused), Running(AllWell with { Paused = true }).IconKey(blinkLit: false));
        Assert.Equal(new TrayIconKey(TrayDot.Red, TrayMark.None), Stopped().IconKey(blinkLit: false));
    }

    [Fact]
    public void Running_says_where_Weir_is()
    {
        Assert.Equal("Running at http://localhost:9347", Running().StatusLine);
        Assert.Equal("Weir - Running at http://localhost:9347", Running().HoverText);
    }

    [Fact]
    public void The_hover_text_is_the_product_name_and_the_status_line()
    {
        Assert.Equal("Weir - Starting...", Starting().HoverText);
        Assert.Equal("Weir - Paused", Running(AllWell with { Paused = true }).HoverText);
        Assert.Equal("Weir - Update ready (1.0.0-rc.12)", Running(update: "1.0.0-rc.12").HoverText);
        Assert.Equal("Weir - Stopped - choose Restart Weir", Stopped().HoverText);
    }

    [Fact]
    public void A_manager_that_does_not_answer_is_named()
    {
        Assert.Equal("Weir - Deluno isn't answering", Running(AllWell with { ManagersUnreachable = ["Deluno"] }).HoverText);
        Assert.Equal("Weir - Deluno on RIG isn't answering", Running(AllWell with { ManagersUnreachable = ["Deluno on RIG"] }).HoverText);
    }

    [Fact]
    public void Several_managers_that_do_not_answer_are_counted()
    {
        Assert.Equal(
            "Weir - 2 media managers aren't answering",
            Running(AllWell with { ManagersUnreachable = ["Deluno", "Sonarr"] }).HoverText);
    }

    [Fact]
    public void A_folder_that_cannot_be_reached_is_named_in_the_words_the_server_gave()
    {
        Assert.Equal(
            "Weir - The watched folder for Movies can't be reached",
            Running(AllWell with { FoldersUnreachable = ["The watched folder for Movies"] }).HoverText);
    }

    [Fact]
    public void Several_folders_that_cannot_be_reached_are_counted()
    {
        Assert.Equal(
            "Weir - 3 folders can't be reached",
            Running(AllWell with { FoldersUnreachable = ["The watched folder for Movies", "The work folder for Movies", "The output folder for Shows"] }).HoverText);
    }

    [Fact]
    public void A_manager_and_a_folder_are_both_said()
    {
        Assert.Equal(
            "Weir - Deluno isn't answering - The output folder for Movies can't be reached",
            Running(AllWell with { ManagersUnreachable = ["Deluno"], FoldersUnreachable = ["The output folder for Movies"] }).HoverText);
    }

    [Fact]
    public void A_paused_Weir_or_one_with_an_update_waiting_still_says_what_does_not_answer()
    {
        Assert.Equal(
            "Weir - Paused - Deluno isn't answering",
            Running(AllWell with { Paused = true, ManagersUnreachable = ["Deluno"] }).HoverText);
        Assert.Equal(
            "Weir - Update ready (2.0.0) - The watched folder for Movies can't be reached",
            Running(AllWell with { FoldersUnreachable = ["The watched folder for Movies"] }, update: "2.0.0").HoverText);
    }

    [Fact]
    public void Nothing_in_what_the_icon_says_counts_files()
    {
        var states = new[]
        {
            Running(), Running(AllWell with { Paused = true }), Running(update: "1.0.0"), Starting(), Stopped(),
            Running(AllWell with { ManagersUnreachable = ["Deluno"], FoldersUnreachable = ["The watched folder for Movies"] }),
            Running(AllWell with { ServerOk = false }),
        };

        Assert.All(states, state => Assert.DoesNotMatch(@"\bfiles?\b", state.HoverText));
    }

    [Fact]
    public void The_hover_text_never_passes_what_Windows_allows_and_is_cut_between_words()
    {
        var longName = string.Join(' ', Enumerable.Repeat("word", 40));
        var state = Running(AllWell with { ManagersUnreachable = [longName] });

        var hover = state.HoverText;

        Assert.True(hover.Length <= TrayState.HoverLimit, hover);
        Assert.EndsWith("…", hover, StringComparison.Ordinal);
        Assert.StartsWith("Weir - word word", hover, StringComparison.Ordinal);
        var full = $"Weir - {state.StatusLine}";
        var kept = hover[..^1];
        Assert.StartsWith(kept, full, StringComparison.Ordinal);
        Assert.Equal(' ', full[kept.Length]);
    }

    [Fact]
    public void A_name_with_no_spaces_that_does_not_fit_costs_the_whole_status_line()
    {
        var state = Running(AllWell with { ManagersUnreachable = [new string('x', 200)] });

        Assert.Equal("Weir…", state.HoverText);
    }

    [Fact]
    public void A_status_that_is_overdue_but_has_been_reported_still_says_where_Weir_is()
    {
        var state = new TrayState(ServerPhase.Running, AllWell, null, 9347, StatusOverdue: true);

        Assert.Equal("Weir - Running at http://localhost:9347", state.HoverText);
    }

    [Fact]
    public void A_hover_text_of_exactly_the_limit_is_left_alone()
    {
        var filler = new string('x', TrayState.HoverLimit - "Weir - ".Length - " isn't answering".Length);
        var state = Running(AllWell with { ManagersUnreachable = [filler] });

        Assert.Equal(TrayState.HoverLimit, state.HoverText.Length);
        Assert.DoesNotContain("…", state.HoverText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_line_is_the_hover_text_without_the_product_name()
    {
        var state = Running(AllWell with { Paused = true, ManagersUnreachable = ["Deluno"] }, update: "2.0.0");

        Assert.Equal($"Weir - {state.StatusLine}", state.HoverText);
    }

    [Fact]
    public void Copy_address_gives_this_pcs_address_until_other_devices_are_let_in()
    {
        Assert.Equal("http://localhost:9347", TrayState.AddressToCopy(ListenScope.ThisPcOnly, 9347, "MEDIA-PC"));
        Assert.Equal("http://MEDIA-PC:9347", TrayState.AddressToCopy(ListenScope.OtherDevices, 9347, "MEDIA-PC"));
    }
}
