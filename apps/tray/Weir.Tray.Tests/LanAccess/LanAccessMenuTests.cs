using Microsoft.Extensions.Time.Testing;
using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;
using Xunit;

namespace Weir.Tray.Tests.LanAccess;

/// <summary>
/// The tray's two LAN access items: what they say while Windows and the server are worked on, what is saved, and which notice the person
/// gets for each way the change can end.
/// </summary>
public sealed class LanAccessMenuTests : IDisposable
{
    private readonly TempDirectory _temp = TempDirectory.AsWeirHome();
    private readonly FakeFirewall _firewall = new();
    private readonly List<LanAccessNotice> _notices = [];
    private readonly List<string> _drawn = [];
    private LanAccessMenu? _menu;

    public void Dispose() => _temp.Dispose();

    private string Home => _temp.Path;

    private string Log => LogFile.ReadAllText(Path.Combine(Home, "tray-host.log"));

    private ListenScope? Saved() => LanAccessSetting.Read(Home, _ => { });

    // A data folder nothing can be saved in: its path runs through a file.
    private string HomeThatIsAFile()
    {
        var file = Path.Combine(Home, "a-file");
        File.WriteAllText(file, "not a folder");
        return Path.Combine(file, "data");
    }

    private LanAccessMenu MenuFor(FakeServer server, string? home = null, CancellationToken cancellationToken = default)
    {
        var sync = new LanAccessSync(home ?? Home, server, _firewall, new FakeTimeProvider());
        _menu = new LanAccessMenu(sync, server, _firewall, _notices.Add, Draw, cancellationToken);
        return _menu;
    }

    // What the two items said each time the tray drew them, so the order of what the person saw can be read back.
    private void Draw() => _drawn.Add($"{_menu!.State.AllowText} | {_menu.State.ThisPcOnlyText}");

    private const string Allow = LanAccessMenuState.AllowLabel;
    private const string ThisPcOnly = LanAccessMenuState.ThisPcOnlyLabel;

    [Fact]
    public async Task Allowing_asks_Windows_first_then_saves_the_choice_and_restarts_the_server_for_the_network()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly);

        await MenuFor(server).AllowAsync();

        Assert.Equal(1, _firewall.Asked);
        Assert.Equal(ListenScope.OtherDevices, Saved());
        Assert.Equal([ListenScope.OtherDevices], server.Moves);
        Assert.Equal(
            [$"Waiting for Windows admin approval... | {ThisPcOnly}", $"Restarting Weir for other devices... | {ThisPcOnly}", $"{Allow} | {ThisPcOnly}"],
            _drawn);
        Assert.Equal(LanAccessNotice.Restarted(ListenScope.OtherDevices, ScopeChange.Applied), Assert.Single(_notices));
    }

    [Fact]
    public async Task While_it_works_neither_item_can_be_clicked_and_both_can_when_it_is_done()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly);
        var enabled = new List<(bool Allow, bool ThisPc)>();
        var sync = new LanAccessSync(Home, server, _firewall, new FakeTimeProvider());
        LanAccessMenu? menu = null;
        menu = new LanAccessMenu(sync, server, _firewall, _notices.Add, () => enabled.Add((menu!.State.AllowEnabled, menu.State.ThisPcOnlyEnabled)), CancellationToken.None);

        await menu.AllowAsync();

        Assert.Equal([(false, false), (false, false), (true, true)], enabled);
    }

    [Fact]
    public async Task Allowing_when_the_server_already_listens_for_the_network_says_Windows_Firewall_now_allows_it()
    {
        var server = new FakeServer(ListenScope.OtherDevices);

        await MenuFor(server).AllowAsync();

        Assert.Equal(1, _firewall.Asked);
        Assert.Equal(LanAccessNotice.FirewallAllowsWeir(), Assert.Single(_notices));
    }

    [Theory]
    [InlineData("Declined")]
    [InlineData("Failed")]
    public async Task Allowing_when_Windows_does_not_make_the_rule_changes_nothing_and_says_why(string windowsAnswered)
    {
        var answer = Enum.Parse<FirewallElevation.Outcome>(windowsAnswered);
        _firewall.Answer = answer;
        var server = new FakeServer(ListenScope.ThisPcOnly);

        await MenuFor(server).AllowAsync();

        Assert.Empty(server.Moves);
        Assert.Null(Saved());
        Assert.Equal(LanAccessNotice.FirewallStepFailed(answer, ListenScope.ThisPcOnly), Assert.Single(_notices));
        Assert.Equal(Allow + " | " + ThisPcOnly, _drawn[^1]);
    }

    [Fact]
    public async Task Allowing_when_the_server_will_not_restart_for_the_network_says_so_and_leaves_the_menu_free()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly) { ResultOfARealMove = ScopeChange.Failed };

        await MenuFor(server).AllowAsync();

        Assert.Equal(LanAccessNotice.Restarted(ListenScope.OtherDevices, ScopeChange.Failed), Assert.Single(_notices));
        Assert.Equal(Allow + " | " + ThisPcOnly, _drawn[^1]);
    }

    [Fact]
    public async Task Allowing_when_the_choice_cannot_be_saved_says_so_and_does_not_touch_the_server()
    {
        var server = new FakeServer(ListenScope.ThisPcOnly);
        await MenuFor(server, HomeThatIsAFile()).AllowAsync();

        Assert.Empty(server.Moves);
        Assert.Equal(LanAccessNotice.NotSaved(), Assert.Single(_notices));
        Assert.Contains("LAN access: the choice could not be saved", Log, StringComparison.Ordinal);
        Assert.Equal(Allow + " | " + ThisPcOnly, _drawn[^1]);
    }

    [Fact]
    public async Task Allowing_when_Weir_is_quitting_stops_quietly_and_leaves_the_menu_free()
    {
        using var quitting = new CancellationTokenSource();
        await quitting.CancelAsync();
        var server = new FakeServer(ListenScope.ThisPcOnly);

        await MenuFor(server, cancellationToken: quitting.Token).AllowAsync();

        Assert.Empty(_notices);
        Assert.Empty(server.Moves);
        Assert.Contains("Allow other devices on your network: stopped, because Weir is quitting or updating.", Log, StringComparison.Ordinal);
        Assert.Equal(Allow + " | " + ThisPcOnly, _drawn[^1]);
    }

    [Fact]
    public async Task Limiting_to_this_PC_saves_the_choice_and_restarts_the_server_with_no_Windows_prompt()
    {
        var server = new FakeServer(ListenScope.OtherDevices);

        await MenuFor(server).LimitToThisPcAsync();

        Assert.Equal(0, _firewall.Asked);
        Assert.Equal(ListenScope.ThisPcOnly, Saved());
        Assert.Equal([ListenScope.ThisPcOnly], server.Moves);
        Assert.Equal(LanAccessNotice.Restarted(ListenScope.ThisPcOnly, ScopeChange.Applied), Assert.Single(_notices));
        Assert.Equal([Allow + " | Restarting Weir for this PC only...", Allow + " | " + ThisPcOnly], _drawn);
    }

    [Fact]
    public async Task Limiting_to_this_PC_when_the_server_will_not_restart_says_nothing_changed()
    {
        var server = new FakeServer(ListenScope.OtherDevices) { ResultOfARealMove = ScopeChange.Failed };

        await MenuFor(server).LimitToThisPcAsync();

        Assert.Equal(LanAccessNotice.Restarted(ListenScope.ThisPcOnly, ScopeChange.Failed), Assert.Single(_notices));
    }

    [Fact]
    public async Task Limiting_to_this_PC_when_the_choice_cannot_be_saved_says_so()
    {
        var server = new FakeServer(ListenScope.OtherDevices);

        await MenuFor(server, HomeThatIsAFile()).LimitToThisPcAsync();

        Assert.Empty(server.Moves);
        Assert.Equal(LanAccessNotice.NotSaved(), Assert.Single(_notices));
    }

    [Fact]
    public async Task Limiting_to_this_PC_when_Weir_is_quitting_stops_quietly()
    {
        using var quitting = new CancellationTokenSource();
        await quitting.CancelAsync();

        await MenuFor(new FakeServer(ListenScope.OtherDevices), cancellationToken: quitting.Token).LimitToThisPcAsync();

        Assert.Empty(_notices);
        Assert.Contains("Only allow this PC: stopped, because Weir is quitting or updating.", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void A_choice_another_process_saved_that_waits_for_Windows_shows_the_wait_and_its_outcome_clears_it()
    {
        var menu = MenuFor(new FakeServer(ListenScope.ThisPcOnly));

        menu.OnWaitingForWindows();
        Assert.Equal("Waiting for Windows admin approval...", menu.State.AllowText);

        menu.OnChangedElsewhere(new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Applied, FirewallElevation.Outcome.Configured));
        Assert.Equal(Allow, menu.State.AllowText);
        Assert.Equal(LanAccessNotice.ForSavedChoice(new SavedChoiceApplied(ListenScope.OtherDevices, ScopeChange.Applied, FirewallElevation.Outcome.Configured)), Assert.Single(_notices));
    }
}
