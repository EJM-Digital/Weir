using Microsoft.Extensions.Time.Testing;
using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests.LanAccess;

/// <summary>
/// The running server follows the saved LAN access choice: a change from the menu or from another process (the web
/// page, <c>--allow-lan</c>) restarts it, no change leaves it alone, and a server that will not start the new way
/// does not leave the saved choice saying something that is not true. A choice for other devices that finds
/// Windows Firewall without a rule for Weir raises the Windows admin prompt, and the server follows the choice
/// whatever the person answers.
/// </summary>
public sealed class LanAccessSyncTests : IDisposable
{
    private readonly TempDirectory _temp = TempDirectory.AsWeirHome();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeFirewall _firewall = new();
    private readonly List<SavedChoiceApplied> _applied = [];
    private int _asking;
    private int _saves;

    public void Dispose() => _temp.Dispose();

    private string Home => _temp.Path;

    private LanAccessSync SyncFor(FakeServer server) => new(Home, server, _firewall, _time);

    private LanAccessWatch Watch() => new(() => _asking++, _applied.Add);

    private ListenScope? Saved() => LanAccessSetting.Read(Home, _ => { });

    // Writes the choice the way another process does, with a modified time no earlier write shares.
    private void SaveFromAnotherProcess(ListenScope scope)
    {
        LanAccessSetting.Write(Home, scope);
        File.SetLastWriteTimeUtc(Path.Combine(Home, LanAccessSetting.FileName), new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(++_saves));
    }

    // -- Saving from the tray's own menu ---------------------------------------

    [Fact]
    public async Task Setting_a_choice_saves_it_and_restarts_the_server_to_match()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);

        var change = await sync.SetAsync(ListenScope.OtherDevices, CancellationToken.None);

        Assert.Equal(ScopeChange.Applied, change);
        Assert.Equal(ListenScope.OtherDevices, server.Scope);
        Assert.Equal(ListenScope.OtherDevices, Saved());
    }

    [Fact]
    public async Task Setting_the_choice_the_server_already_has_does_not_restart_it()
    {
        var server = new FakeServer(ListenScope.OtherDevices);
        using var sync = SyncFor(server);

        var change = await sync.SetAsync(ListenScope.OtherDevices, CancellationToken.None);

        Assert.Equal(ScopeChange.Unchanged, change);
        Assert.Equal(ListenScope.OtherDevices, Saved());
    }

    [Fact]
    public async Task A_server_that_will_not_start_the_new_way_leaves_the_old_choice_saved()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly) { ResultOfARealMove = ScopeChange.Failed };
        using var sync = SyncFor(server);

        var change = await sync.SetAsync(ListenScope.OtherDevices, CancellationToken.None);

        Assert.Equal(ScopeChange.Failed, change);
        Assert.Equal(ListenScope.ThisPcOnly, Saved());
    }

    [Fact]
    public async Task The_menu_saving_a_choice_never_raises_the_admin_prompt_itself()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);

        await sync.SetAsync(ListenScope.OtherDevices, CancellationToken.None);
        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(0, _firewall.Asked);
    }

    // -- Noticing a change made by another process -------------------------------

    [Fact]
    public async Task A_choice_another_process_saved_restarts_the_server_and_is_reported()
    {
        _firewall.Allows = true;
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(ListenScope.OtherDevices, server.Scope);
        Assert.Equal([new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Applied, null)], _applied);
        Assert.Equal(0, _firewall.Asked);
    }

    [Fact]
    public async Task A_saved_choice_the_server_already_follows_leaves_it_alone()
    {
        var server = new FakeServer(ListenScope.OtherDevices);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);
        _firewall.Allows = true;

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Empty(server.Moves);
        Assert.Empty(_applied);
    }

    [Fact]
    public async Task With_no_choice_saved_the_server_is_left_alone()
    {
        var server = new FakeServer(ListenScope.OtherDevices);
        using var sync = SyncFor(server);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Empty(server.Moves);
        Assert.Empty(_applied);
    }

    [Fact]
    public async Task A_change_the_server_cannot_follow_is_reported_once_and_the_old_choice_is_put_back()
    {
        _firewall.Allows = true;
        var server = new FakeServer(ListenScope.ThisPcOnly) { ResultOfARealMove = ScopeChange.Failed };
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);

        await sync.CheckAsync(Watch(), CancellationToken.None);
        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal([ScopeChange.Failed], _applied.Select(applied => applied.Change));
        Assert.Equal(ListenScope.ThisPcOnly, Saved());
        Assert.Single(server.Moves);
    }

    [Fact]
    public async Task A_saved_choice_that_cannot_be_put_back_is_logged_and_the_server_is_left_as_it_is()
    {
        var folder = Path.Combine(Home, "refused");
        Directory.CreateDirectory(folder);
        LanAccessSetting.Write(folder, ListenScope.OtherDevices);
        _firewall.Allows = true;
        var server = new FakeServer(ListenScope.ThisPcOnly) { ResultOfARealMove = ScopeChange.Failed };
        using var sync = new LanAccessSync(folder, server, _firewall, _time);
        using var denied = new DeniedFolder(folder, System.Security.AccessControl.FileSystemRights.CreateFiles);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal([ScopeChange.Failed], _applied.Select(applied => applied.Change));
        Assert.Contains("the saved choice could not be put back", LogFile.ReadAllText(Path.Combine(Home, TrayLog.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watching_notices_a_change_when_the_poll_interval_passes()
    {
        _firewall.Allows = true;
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        using var stop = new CancellationTokenSource();
        var restarted = new TaskCompletionSource<ListenScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watching = sync.WatchAsync(new LanAccessWatch(() => { }, applied => restarted.TrySetResult(applied.Scope)), stop.Token);

        SaveFromAnotherProcess(ListenScope.OtherDevices);
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(ListenScope.OtherDevices, await restarted.Task);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
    }

    // -- The Windows admin prompt for a choice another process saved -------------

    [Fact]
    public async Task A_choice_for_other_devices_without_a_firewall_rule_asks_windows_first_and_then_restarts()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(1, _firewall.Asked);
        Assert.Equal(1, _asking);
        Assert.Equal(ListenScope.OtherDevices, server.Scope);
        Assert.Equal(
            [new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Applied, FirewallElevation.Outcome.Configured)],
            _applied);
    }

    [Fact]
    public async Task Declining_the_admin_prompt_still_restarts_for_other_devices_and_says_the_firewall_was_not_changed()
    {
        _firewall.Answer = FirewallElevation.Outcome.Declined;
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(ListenScope.OtherDevices, server.Scope);
        Assert.Equal(ListenScope.OtherDevices, Saved());
        Assert.Equal(
            [new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Applied, FirewallElevation.Outcome.Declined)],
            _applied);
    }

    [Fact]
    public async Task A_declined_prompt_is_not_raised_again_until_the_choice_is_saved_again()
    {
        _firewall.Answer = FirewallElevation.Outcome.Declined;
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);

        await sync.CheckAsync(Watch(), CancellationToken.None);
        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(1, _firewall.Asked);
    }

    [Fact]
    public async Task Saving_the_same_choice_again_asks_windows_again_without_restarting_the_server()
    {
        _firewall.Answer = FirewallElevation.Outcome.Declined;
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);
        await sync.CheckAsync(Watch(), CancellationToken.None);
        _applied.Clear();
        _firewall.Answer = FirewallElevation.Outcome.Configured;

        SaveFromAnotherProcess(ListenScope.OtherDevices);
        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(2, _firewall.Asked);
        Assert.Equal(
            [new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Unchanged, FirewallElevation.Outcome.Configured)],
            _applied);
    }

    [Fact]
    public async Task Once_windows_has_answered_the_choice_is_saved_again_so_the_server_can_tell_the_page()
    {
        var server = new FakeServer(ListenScope.OtherDevices);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);
        var savedBefore = File.GetLastWriteTimeUtc(Path.Combine(Home, LanAccessSetting.FileName));

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(Home, LanAccessSetting.FileName)) > savedBefore);
        Assert.Equal(ListenScope.OtherDevices, Saved());
        Assert.Empty(server.Moves);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(1, _firewall.Asked);
    }

    [Fact]
    public async Task A_choice_that_raises_no_prompt_is_left_as_it_was_saved()
    {
        _firewall.Allows = true;
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);
        var savedBefore = File.GetLastWriteTimeUtc(Path.Combine(Home, LanAccessSetting.FileName));

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(savedBefore, File.GetLastWriteTimeUtc(Path.Combine(Home, LanAccessSetting.FileName)));
    }

    [Fact]
    public async Task A_choice_for_other_devices_with_a_firewall_rule_in_place_does_not_ask_windows()
    {
        _firewall.Allows = true;
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(0, _firewall.Asked);
        Assert.Equal(0, _asking);
    }

    [Fact]
    public async Task A_choice_for_this_pc_only_never_asks_windows()
    {
        var server = new FakeServer(ListenScope.OtherDevices);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.ThisPcOnly);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(0, _firewall.Asked);
        Assert.Equal(ListenScope.ThisPcOnly, server.Scope);
    }

    [Fact]
    public async Task A_choice_already_saved_when_the_tray_starts_does_not_raise_the_admin_prompt()
    {
        SaveFromAnotherProcess(ListenScope.OtherDevices);
        var server = new FakeServer(ListenScope.OtherDevices);
        using var sync = SyncFor(server);

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(0, _firewall.Asked);
        Assert.Empty(_applied);
    }

    [Fact]
    public async Task A_server_that_will_not_start_after_the_prompt_puts_the_old_choice_back_without_asking_again()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly) { ResultOfARealMove = ScopeChange.Failed };
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);

        await sync.CheckAsync(Watch(), CancellationToken.None);
        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(1, _firewall.Asked);
        Assert.Equal(ListenScope.ThisPcOnly, Saved());
    }

    // -- A choice that is being replaced ------------------------------------

    [Fact]
    public async Task A_saved_choice_that_cannot_be_read_just_now_is_waited_for_not_taken_for_a_choice()
    {
        var server = new FakeServer(ListenScope.OtherDevices);
        _firewall.Allows = true;
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.ThisPcOnly);

        // Another process is renaming a new file over the choice: Windows refuses the open meanwhile.
        using (new FileStream(Path.Combine(Home, LanAccessSetting.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await sync.CheckAsync(Watch(), CancellationToken.None);

            Assert.Empty(server.Moves);
            Assert.Empty(_applied);
            Assert.Equal(ListenScope.OtherDevices, server.Scope);
        }

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal([ListenScope.ThisPcOnly], server.Moves);
        Assert.Equal(ListenScope.ThisPcOnly, server.Scope);
    }

    [Fact]
    public async Task A_choice_that_was_busy_is_not_a_new_save_when_it_is_read_again()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly);
        using var sync = SyncFor(server);
        SaveFromAnotherProcess(ListenScope.OtherDevices);
        await sync.CheckAsync(Watch(), CancellationToken.None);
        var asked = _firewall.Asked;

        using (new FileStream(Path.Combine(Home, LanAccessSetting.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await sync.CheckAsync(Watch(), CancellationToken.None);
        }

        await sync.CheckAsync(Watch(), CancellationToken.None);

        Assert.Equal(asked, _firewall.Asked);
    }
}
