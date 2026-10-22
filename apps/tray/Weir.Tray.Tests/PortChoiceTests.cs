using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// A port that moved on its own would break every bookmark to Weir. These pin the rules: chosen
/// once, saved, reused, never moved without asking — and never a dialog with nobody at a desktop
/// to answer it.
/// </summary>
public sealed class PortChoiceTests : IDisposable
{
    private readonly TempDirectory _temp = TempDirectory.AsWeirHome();

    public void Dispose() => _temp.Dispose();

    private string Home => _temp.Path;

    private static Func<int, bool> Busy(params int[] ports) => p => ports.Contains(p);

    private static Func<PortPrompt, int?> NeverAsk =>
        prompt => throw new Xunit.Sdk.XunitException($"The dialog was shown ({prompt.Reason}) when it must not be.");

    [Fact]
    public void The_default_is_weirs_own_port()
    {
        Assert.Equal(9347, PortChoice.DefaultPort);
    }

    // -- Supplied without a person -----------------------------------------

    [Theory]
    [InlineData(new[] { "--port", "9400" }, "9400")]
    [InlineData(new[] { "--no-browser", "--port=9401" }, "9401")]
    [InlineData(new[] { "--PORT", "9402" }, "9402")]
    public void Port_argument_is_read(string[] args, string expected)
    {
        var supplied = PortChoice.SuppliedPort(args, _ => null);
        Assert.Equal((expected, "--port"), supplied);
    }

    [Fact]
    public void Port_argument_wins_over_the_environment()
    {
        var supplied = PortChoice.SuppliedPort(["--port", "9400"], _ => "9500");
        Assert.Equal(("9400", "--port"), supplied);
    }

    [Fact]
    public void Environment_variable_is_read_when_there_is_no_argument()
    {
        var supplied = PortChoice.SuppliedPort(["--no-browser"], name => name == "WEIR_PORT" ? "9500" : null);
        Assert.Equal(("9500", "WEIR_PORT"), supplied);
    }

    [Fact]
    public void A_supplied_port_is_used_and_saved_without_asking_even_on_a_desktop()
    {
        var decision = PortChoice.Decide(("9400", "--port"), saved: null, interactive: true, Busy(), NeverAsk);

        Assert.Equal(9400, decision.Port);
        Assert.True(decision.Save);
    }

    [Fact]
    public void A_supplied_port_overrides_the_saved_one()
    {
        var decision = PortChoice.Decide(("9400", "WEIR_PORT"), saved: 9347, interactive: false, Busy(), NeverAsk);

        Assert.Equal(9400, decision.Port);
        Assert.True(decision.Save);
    }

    [Fact]
    public void A_supplied_port_equal_to_the_saved_one_is_not_rewritten()
    {
        var decision = PortChoice.Decide(("9347", "--port"), saved: 9347, interactive: false, Busy(), NeverAsk);

        Assert.Equal(9347, decision.Port);
        Assert.False(decision.Save);
    }

    [Fact]
    public void A_supplied_port_that_is_busy_is_still_honoured_not_swapped()
    {
        var decision = PortChoice.Decide(("9400", "--port"), saved: null, interactive: false, Busy(9400), NeverAsk);

        Assert.Equal(9400, decision.Port);
        Assert.Contains("another program", decision.Reason);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("")]
    public void An_unusable_supplied_port_is_logged_and_ignored(string text)
    {
        var decision = PortChoice.Decide((text, "--port"), saved: 9360, interactive: false, Busy(), NeverAsk);

        Assert.Equal(9360, decision.Port);
        Assert.Contains("Ignoring --port", decision.Reason);
    }

    // -- Headless -------------------------------------------------------------

    [Fact]
    public void Headless_first_run_uses_the_default_and_saves_it()
    {
        var decision = PortChoice.Decide(null, saved: null, interactive: false, Busy(), NeverAsk);

        Assert.Equal(9347, decision.Port);
        Assert.True(decision.Save);
        Assert.Contains("no interactive desktop", decision.Reason);
    }

    [Fact]
    public void Headless_first_run_with_the_default_busy_takes_the_first_free_port_above_it_and_says_so()
    {
        var decision = PortChoice.Decide(null, saved: null, interactive: false, Busy(9347, 9348), NeverAsk);

        Assert.Equal(9349, decision.Port);
        Assert.True(decision.Save);
        Assert.Contains("9347 is in use", decision.Reason);
    }

    [Fact]
    public void Headless_start_with_the_saved_port_busy_does_not_move()
    {
        var decision = PortChoice.Decide(null, saved: 9347, interactive: false, Busy(9347), NeverAsk);

        Assert.Null(decision.Port);
        Assert.False(decision.Save);
        Assert.Contains("does not move", decision.Reason);
    }

    // -- A person at a desktop -------------------------------------------------

    [Fact]
    public void Interactive_first_run_asks_with_the_default_prefilled()
    {
        PortPrompt? seen = null;
        var decision = PortChoice.Decide(null, saved: null, interactive: true, Busy(), p => { seen = p; return 9347; });

        Assert.Equal(new PortPrompt(PortPromptReason.FirstRun, 9347, false, 9347), seen);
        Assert.Equal(9347, decision.Port);
        Assert.True(decision.Save);
    }

    [Fact]
    public void Interactive_first_run_with_the_default_busy_suggests_a_free_one()
    {
        PortPrompt? seen = null;
        PortChoice.Decide(null, saved: null, interactive: true, Busy(9347), p => { seen = p; return 9348; });

        Assert.Equal(new PortPrompt(PortPromptReason.FirstRun, 9347, true, 9348), seen);
    }

    [Fact]
    public void Quitting_the_first_run_dialog_does_not_start_or_save()
    {
        var decision = PortChoice.Decide(null, saved: null, interactive: true, Busy(), _ => null);

        Assert.Null(decision.Port);
        Assert.False(decision.Save);
    }

    [Fact]
    public void A_later_start_reuses_the_saved_port_without_asking()
    {
        var decision = PortChoice.Decide(null, saved: 9360, interactive: true, Busy(), NeverAsk);

        Assert.Equal(9360, decision.Port);
        Assert.False(decision.Save);
    }

    [Fact]
    public void A_later_start_with_the_saved_port_busy_asks_instead_of_moving()
    {
        PortPrompt? seen = null;
        var decision = PortChoice.Decide(null, saved: 9360, interactive: true, Busy(9360), p => { seen = p; return 9361; });

        Assert.Equal(new PortPrompt(PortPromptReason.SavedPortBusy, 9360, true, 9361), seen);
        Assert.Equal(9361, decision.Port);
        Assert.True(decision.Save);
    }

    // -- Dialog validation ------------------------------------------------------

    [Theory]
    [InlineData("", "Enter a port number.")]
    [InlineData("0", "A port is a whole number from 1 to 65535.")]
    [InlineData("65536", "A port is a whole number from 1 to 65535.")]
    [InlineData("12.5", "A port is a whole number from 1 to 65535.")]
    [InlineData("port", "A port is a whole number from 1 to 65535.")]
    public void Invalid_entries_are_explained(string text, string expected)
    {
        Assert.Equal(expected, PortChoice.Validate(text, null, Busy()));
    }

    [Fact]
    public void A_busy_entry_is_refused()
    {
        Assert.Contains("already using port 9400", PortChoice.Validate("9400", null, Busy(9400)));
    }

    [Fact]
    public void Weirs_own_current_port_is_not_another_program()
    {
        Assert.Null(PortChoice.Validate("9400", allowedInUse: 9400, Busy(9400)));
    }

    [Fact]
    public void A_free_entry_is_accepted()
    {
        Assert.Null(PortChoice.Validate(" 9400 ", null, Busy()));
    }

    // -- Saved on disk ---------------------------------------------------------

    [Fact]
    public void Nothing_saved_is_null()
    {
        Assert.Equal(new PortChoice.SavedPort(false, null), PortChoice.LoadSaved(Home));
    }

    [Fact]
    public void A_saved_port_round_trips()
    {
        PortChoice.Save(Home, 9400);

        Assert.Equal(9400, PortChoice.LoadSaved(Home).Port);
        Assert.Equal("9400", File.ReadAllText(Path.Combine(Home, "port.txt")));
        Assert.Empty(Directory.GetFiles(Home, "*.tmp"));
    }

    [Fact]
    public void A_damaged_saved_port_reads_as_not_chosen()
    {
        File.WriteAllText(Path.Combine(Home, "port.txt"), "not a port");

        Assert.Equal(new PortChoice.SavedPort(false, null), PortChoice.LoadSaved(Home));
    }

    [Fact]
    public void A_saved_port_that_stays_unreadable_is_busy_never_not_chosen()
    {
        PortChoice.Save(Home, 9400);
        using var held = new FileStream(Path.Combine(Home, "port.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        var pauses = 0;

        var saved = PortChoice.LoadSaved(Home, _ => pauses++);

        Assert.True(saved.Busy);
        Assert.Null(saved.Port);
        Assert.Equal(ReplacedFile.Attempts - 1, pauses);
    }

    [Fact]
    public void A_saved_port_that_is_refused_for_a_moment_is_read_when_it_comes_back()
    {
        PortChoice.Save(Home, 9400);
        var held = new FileStream(Path.Combine(Home, "port.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        var pauses = 0;

        var saved = PortChoice.LoadSaved(Home, _ =>
        {
            if (++pauses == 2)
            {
                held.Dispose();
            }
        });

        Assert.Equal(new PortChoice.SavedPort(false, 9400), saved);
    }

    // -- The machine -------------------------------------------------------------

    [Fact]
    public void A_loopback_only_listener_still_counts_as_in_use()
    {
        // The server binds every interface, so a loopback-only listener still blocks it. Port 0 lets the system pick one nobody
        // holds, and the listener is kept for the whole test, so no other program can take the port while it is checked.
        // Loopback, not 0.0.0.0: listening on every interface makes Windows Firewall put a prompt on the desktop of whoever
        // runs the tests.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(PortChoice.IsInUse(port));
    }

    [Fact]
    public void A_port_that_nothing_listens_on_is_not_in_use()
    {
        // The system picks the port, so it is one Windows is willing to hand out: not listened on, not held by an outgoing connection and
        // not inside a range Windows has reserved (Hyper-V and WSL reserve some, and a fixed number can fall in one). Only a socket that
        // was bound and never listened on has held it, so letting it go leaves nothing behind that could still be counted against it.
        int free;
        using (var holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true })
        {
            holder.Bind(new IPEndPoint(IPAddress.Any, 0));
            free = ((IPEndPoint)holder.LocalEndPoint!).Port;
        }

        Assert.False(PortChoice.IsInUse(free));
    }

    [Fact]
    public void A_port_argument_with_nothing_after_it_is_supplied_as_empty_text()
    {
        Assert.Equal(("", "--port"), PortChoice.SuppliedPort(["--no-browser", "--port"], _ => null));
    }

    [Fact]
    public void No_text_is_no_port()
    {
        Assert.Null(PortChoice.ParsePort(null));
    }

    [Fact]
    public void A_later_start_with_the_saved_port_busy_and_the_dialog_closed_does_not_start_or_save()
    {
        var decision = PortChoice.Decide(null, saved: 9360, interactive: true, Busy(9360), _ => null);

        Assert.Null(decision.Port);
        Assert.False(decision.Save);
        Assert.Contains("quit without choosing another", decision.Reason);
    }

    [Fact]
    public void A_later_start_with_the_saved_port_busy_and_the_same_port_chosen_again_saves_nothing()
    {
        var decision = PortChoice.Decide(null, saved: 9360, interactive: true, Busy(9360), _ => 9360);

        Assert.Equal(9360, decision.Port);
        Assert.False(decision.Save);
    }

    // -- The desktop and the listener table, answered by stand-ins --------------

    [Theory]
    [InlineData(true, 1, true, true)]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 0, true, false)]
    [InlineData(true, 1, false, false)]
    public void A_person_can_answer_only_in_an_interactive_process_outside_session_zero_on_a_visible_window_station(
        bool processInteractive, int session, bool windowStation, bool expected)
    {
        Assert.Equal(expected, PortChoice.HasInteractiveDesktop(() => processInteractive, () => session, () => windowStation));
    }

    [Fact]
    public void When_the_session_cannot_be_read_nobody_is_asked_and_the_log_says_why()
    {
        var interactive = PortChoice.HasInteractiveDesktop(() => true, () => throw new InvalidOperationException("no process"), () => true);

        Assert.False(interactive);
        Assert.Contains("Could not tell whether this is an interactive desktop (no process); treating it as not.", File.ReadAllText(Path.Combine(Home, "tray-host.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_desktop_check_answers_without_failing()
    {
        var first = PortChoice.HasInteractiveDesktop();

        Assert.Equal(first, PortChoice.HasInteractiveDesktop());
    }

    [Fact]
    public void This_process_names_its_session_and_its_window_station()
    {
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().SessionId, PortChoice.CurrentSessionId());
        Assert.Equal(System.Windows.Forms.SystemInformation.UserInteractive, PortChoice.WindowStationIsInteractive());
    }

    [Fact]
    public void A_port_in_the_listener_table_is_in_use_without_a_bind()
    {
        var inUse = PortChoice.IsInUse(9350, () => [new IPEndPoint(IPAddress.Loopback, 9350)], _ => throw new Xunit.Sdk.XunitException("The port was bound when the table already named it."));

        Assert.True(inUse);
    }

    [Fact]
    public void A_listener_table_that_cannot_be_read_leaves_the_answer_to_the_bind_and_the_log_says_so()
    {
        var bound = new List<int>();

        var inUse = PortChoice.IsInUse(9350, () => throw new NetworkInformationException(5), bound.Add);

        Assert.False(inUse);
        Assert.Equal([9350], bound);
        Assert.Contains("checking port 9350 by binding it", File.ReadAllText(Path.Combine(Home, "tray-host.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_port_that_cannot_be_bound_is_in_use()
    {
        Assert.True(PortChoice.IsInUse(9350, () => [], _ => throw new SocketException((int)SocketError.AddressAlreadyInUse)));
    }
}
