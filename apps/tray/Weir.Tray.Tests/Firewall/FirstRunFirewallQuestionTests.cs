using Weir.Tray.Firewall;
using Weir.Tray.LanAccess;
using Weir.Tray.Tests.LanAccess;
using Xunit;

namespace Weir.Tray.Tests.Firewall;

/// <summary>
/// "Allow Weir on your network?" is the one Windows admin prompt Weir raises on its own. It is asked once, at the first run with somebody at
/// the desktop; the answer is kept even when it is no; and only a yes that ends with the rule in place turns the network on.
/// </summary>
public sealed class FirstRunFirewallQuestionTests : IDisposable
{
    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly FakeFirewall _firewall = new();
    private int _asked;

    public void Dispose() => _home.Dispose();

    private ListenScope? Chosen() => LanAccessSetting.Read(_home.Path, _ => { });

    private string Recorded() => File.ReadAllText(Path.Combine(_home.Path, FirewallPromptFile.FileName));

    private void Ask(bool answer, string[]? args = null, bool atTheDesktop = true) =>
        FirewallInstallHooks.AskOnFirstRun(args ?? [], () => atTheDesktop, () =>
        {
            _asked++;
            return answer;
        }, _firewall, _home.Path);

    [Fact]
    public void A_yes_that_ends_with_the_rule_in_place_turns_the_network_on_and_is_kept()
    {
        Ask(answer: true);

        Assert.Equal(1, _asked);
        Assert.Equal(1, _firewall.Asked);
        Assert.Equal("Configured", Recorded());
        Assert.Equal(ListenScope.OtherDevices, Chosen());
    }

    [Theory]
    [InlineData("Declined")]
    [InlineData("Failed")]
    public void A_yes_that_Windows_does_not_carry_out_is_kept_and_leaves_Weir_to_this_PC(string windowsAnswered)
    {
        _firewall.Answer = Enum.Parse<FirewallElevation.Outcome>(windowsAnswered);

        Ask(answer: true);

        Assert.Equal(windowsAnswered, Recorded());
        Assert.Null(Chosen());
    }

    [Fact]
    public void A_no_is_kept_without_raising_the_Windows_prompt()
    {
        Ask(answer: false);

        Assert.Equal(1, _asked);
        Assert.Equal(0, _firewall.Asked);
        Assert.Equal("Declined", Recorded());
        Assert.Null(Chosen());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_person_who_has_answered_is_not_asked_again(bool firstAnswer)
    {
        Ask(firstAnswer);
        var firewallAsked = _firewall.Asked;

        Ask(answer: true);

        Assert.Equal(1, _asked);
        Assert.Equal(firewallAsked, _firewall.Asked);
    }

    [Fact]
    public void A_silent_start_asks_nothing_and_records_nothing()
    {
        Ask(answer: true, args: [Program.SilentArgument]);

        Assert.Equal(0, _asked);
        Assert.False(File.Exists(Path.Combine(_home.Path, FirewallPromptFile.FileName)));
        Assert.Contains("First-run firewall prompt skipped", File.ReadAllText(Path.Combine(_home.Path, "tray-host.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_with_nobody_at_the_desktop_asks_nothing_and_records_nothing()
    {
        Ask(answer: true, atTheDesktop: false);

        Assert.Equal(0, _asked);
        Assert.False(File.Exists(Path.Combine(_home.Path, FirewallPromptFile.FileName)));
    }

    [Fact]
    public void An_answer_that_cannot_be_kept_is_logged_and_does_not_stop_the_start()
    {
        var file = Path.Combine(_home.Path, "a-file");
        File.WriteAllText(file, "not a folder");

        FirewallInstallHooks.AskOnFirstRun([], () => true, () => false, _firewall, Path.Combine(file, "data"));

        Assert.Contains("First-run firewall prompt could not record its answer", File.ReadAllText(Path.Combine(_home.Path, "tray-host.log")), StringComparison.Ordinal);
    }
}
