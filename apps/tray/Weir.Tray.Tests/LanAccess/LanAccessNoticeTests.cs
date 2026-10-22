using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests.LanAccess;

/// <summary>What the person is told after each LAN access change: what now holds, and that Weir restarted.</summary>
public sealed class LanAccessNoticeTests
{
    [Fact]
    public void Opening_weir_to_the_network_says_it_restarted()
    {
        var notice = LanAccessNotice.Restarted(ListenScope.OtherDevices, ScopeChange.Applied);

        Assert.Equal("Other devices on your network can now reach Weir. Weir restarted to apply this.", notice.Text);
        Assert.False(notice.IsWarning);
    }

    [Fact]
    public void Limiting_weir_to_this_pc_says_it_restarted()
    {
        var notice = LanAccessNotice.Restarted(ListenScope.ThisPcOnly, ScopeChange.Applied);

        Assert.Equal("Only this PC can reach Weir now. Weir restarted to apply this.", notice.Text);
        Assert.False(notice.IsWarning);
    }

    [Theory]
    [InlineData((int)ListenScope.OtherDevices, "only this PC can reach it")]
    [InlineData((int)ListenScope.ThisPcOnly, "nothing changed")]
    public void A_restart_that_failed_is_a_warning_that_says_what_still_holds_and_where_to_look(int target, string whatHolds)
    {
        var notice = LanAccessNotice.Restarted((ListenScope)target, ScopeChange.Failed);

        Assert.True(notice.IsWarning);
        Assert.Contains(whatHolds, notice.Text, StringComparison.Ordinal);
        Assert.Contains("tray-host.log", notice.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)ListenScope.OtherDevices, "Other devices on your network can already reach Weir.")]
    [InlineData((int)ListenScope.ThisPcOnly, "Only this PC could already reach Weir.")]
    public void A_change_that_was_already_in_place_says_so_without_claiming_a_restart(int target, string text)
    {
        var notice = LanAccessNotice.Restarted((ListenScope)target, ScopeChange.Unchanged);

        Assert.Equal(text, notice.Text);
        Assert.False(notice.IsWarning);
    }

    [Fact]
    public void Declining_the_windows_prompt_while_only_this_pc_can_connect_says_others_still_cannot_reach_weir()
    {
        var notice = LanAccessNotice.FirewallStepFailed(FirewallElevation.Outcome.Declined, ListenScope.ThisPcOnly);

        Assert.Equal("Windows admin access was not granted, so other devices still cannot reach Weir.", notice.Text);
        Assert.True(notice.IsWarning);
    }

    [Fact]
    public void Declining_the_windows_prompt_while_other_devices_can_connect_says_the_firewall_was_not_changed()
    {
        var notice = LanAccessNotice.FirewallStepFailed(FirewallElevation.Outcome.Declined, ListenScope.OtherDevices);

        Assert.Equal("Windows admin access was not granted, so Windows Firewall was not changed.", notice.Text);
    }

    [Fact]
    public void A_failed_windows_step_points_at_the_log()
    {
        var notice = LanAccessNotice.FirewallStepFailed(FirewallElevation.Outcome.Failed, ListenScope.ThisPcOnly);

        Assert.StartsWith("Could not allow other devices on your network.", notice.Text, StringComparison.Ordinal);
        Assert.Contains("tray-host.log", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_choice_that_could_not_be_saved_is_a_warning_that_nothing_changed()
    {
        var notice = LanAccessNotice.NotSaved();

        Assert.True(notice.IsWarning);
        Assert.Contains("still set up the way it was", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_choice_saved_elsewhere_and_allowed_through_the_firewall_reads_like_the_menu_does()
    {
        var applied = new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Applied, FirewallElevation.Outcome.Configured);

        Assert.Equal(LanAccessNotice.Restarted(ListenScope.OtherDevices, ScopeChange.Applied), LanAccessNotice.ForSavedChoice(applied));
    }

    [Fact]
    public void Declining_the_prompt_for_a_choice_saved_elsewhere_says_the_firewall_still_blocks_and_where_to_try_again()
    {
        var applied = new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Applied, FirewallElevation.Outcome.Declined);

        var notice = LanAccessNotice.ForSavedChoice(applied);

        Assert.True(notice.IsWarning);
        Assert.Contains("Windows Firewall still blocks them", notice.Text, StringComparison.Ordinal);
        Assert.Contains("try again", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restart_that_failed_after_the_prompt_is_reported_as_the_restart_failing()
    {
        var applied = new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Failed, FirewallElevation.Outcome.Configured);

        Assert.Equal(LanAccessNotice.Restarted(ListenScope.OtherDevices, ScopeChange.Failed), LanAccessNotice.ForSavedChoice(applied));
    }

    [Fact]
    public void Declining_the_prompt_when_asking_again_says_the_firewall_was_not_changed()
    {
        var applied = new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Unchanged, FirewallElevation.Outcome.Declined);

        Assert.Equal(LanAccessNotice.FirewallStepFailed(FirewallElevation.Outcome.Declined, ListenScope.OtherDevices), LanAccessNotice.ForSavedChoice(applied));
    }

    [Fact]
    public void Allowing_the_firewall_when_asking_again_says_windows_now_allows_weir()
    {
        var applied = new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Unchanged, FirewallElevation.Outcome.Configured);

        Assert.Equal(LanAccessNotice.FirewallAllowsWeir(), LanAccessNotice.ForSavedChoice(applied));
    }

    [Fact]
    public void A_choice_carried_out_while_the_windows_step_failed_says_the_firewall_was_not_changed_and_where_to_look()
    {
        var notice = LanAccessNotice.ForSavedChoice(new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Applied, FirewallElevation.Outcome.Failed));

        Assert.Equal("Weir now listens for other devices, but Windows Firewall was not changed, so it still blocks them. See tray-host.log in the data folder.", notice.Text);
        Assert.True(notice.IsWarning);
        Assert.True(notice.NamesLog);
    }

    [Theory]
    [InlineData((int)FirewallElevation.Outcome.Declined)]
    [InlineData((int)FirewallElevation.Outcome.Failed)]
    public void A_windows_step_that_failed_with_the_server_left_as_it_was_is_worded_by_the_step(int outcome)
    {
        var notice = LanAccessNotice.ForSavedChoice(new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Unchanged, (FirewallElevation.Outcome)outcome));

        Assert.Equal(LanAccessNotice.FirewallStepFailed((FirewallElevation.Outcome)outcome, ListenScope.OtherDevices), notice);
    }
}
