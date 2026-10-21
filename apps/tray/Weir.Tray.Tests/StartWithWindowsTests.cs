using Microsoft.Win32;
using Xunit;

namespace Weir.Tray.Tests;

/// <summary>
/// Nothing starts with Windows until the person says yes: at the first-run question, or from the tray menu. Updates
/// and installs never turn it on. These run against a throw-away key under HKCU, never the real Run key.
/// </summary>
public sealed class StartWithWindowsTests : IDisposable
{
    private const string Executable = @"C:\Users\someone\AppData\Local\Weir\current\Weir.exe";

    private readonly TempDirectory _home = TempDirectory.AsWeirHome();
    private readonly TempDirectory _startupFolder = TempDirectory.Create();
    private readonly TempRegistryKey _run = TempRegistryKey.Under(@"Software\WeirTrayTests");
    private readonly TempRegistryKey _answer = TempRegistryKey.Under(@"Software\WeirTrayTests");

    public void Dispose()
    {
        _answer.Dispose();
        _run.Dispose();
        _startupFolder.Dispose();
        _home.Dispose();
    }

    private StartupRegistration Registration(string? executable = Executable) =>
        new(_run.Path, new StartWithWindowsAnswer(_answer.Path), _startupFolder.Path, executable);

    private string? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_run.Path);
        return key?.GetValue("Weir") as string;
    }

    [Fact]
    public void Nothing_starts_with_Windows_before_anyone_chooses()
    {
        Assert.False(Registration().IsEnabled);
        Assert.Null(RunValue());
    }

    [Fact]
    public void Turning_it_on_starts_Weir_at_sign_in_without_opening_a_browser()
    {
        Assert.True(Registration().Enable());

        Assert.True(Registration().IsEnabled);
        Assert.Equal($"\"{Executable}\" --no-browser", RunValue());
    }

    [Fact]
    public void The_menu_tick_switches_it_on_and_then_off()
    {
        var registration = Registration();

        registration.Toggle();
        Assert.True(registration.IsEnabled);

        registration.Toggle();
        Assert.False(registration.IsEnabled);
        Assert.Null(RunValue());
    }

    [Fact]
    public void Turning_it_off_removes_the_entry()
    {
        var registration = Registration();
        registration.Enable();

        registration.Disable();

        Assert.False(registration.IsEnabled);
        Assert.Null(RunValue());
    }

    [Fact]
    public void Turning_it_off_when_it_was_never_on_is_harmless()
    {
        Registration().Disable();

        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void A_startup_folder_shortcut_would_start_a_second_copy_so_turning_it_on_removes_it()
    {
        var shortcut = Path.Combine(_startupFolder.Path, "Weir.lnk");
        File.WriteAllText(shortcut, "x");

        Registration().Enable();

        Assert.False(File.Exists(shortcut));
    }

    [Fact]
    public void Without_a_program_to_start_nothing_is_turned_on()
    {
        Assert.False(Registration(executable: null).Enable());
        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void An_update_points_an_entry_that_is_there_at_the_new_program()
    {
        Registration(@"C:\old\Weir.exe").Enable();

        Registration().RefreshIfEnabled();

        Assert.Equal($"\"{Executable}\" --no-browser", RunValue());
    }

    [Fact]
    public void An_update_never_turns_it_on()
    {
        Registration().RefreshIfEnabled();

        Assert.False(Registration().IsEnabled);
        Assert.Null(RunValue());
    }

    [Fact]
    public void The_first_run_asks_once_and_a_yes_turns_it_on()
    {
        var asked = 0;

        StartWithWindowsPrompt.AskOnFirstRun([], () => true, () => { asked++; return true; }, Registration());

        Assert.Equal(1, asked);
        Assert.True(Registration().IsEnabled);
    }

    [Fact]
    public void A_no_leaves_it_off()
    {
        StartWithWindowsPrompt.AskOnFirstRun([], () => true, () => false, Registration());

        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void A_silent_install_asks_nothing_and_leaves_it_off()
    {
        var asked = false;

        StartWithWindowsPrompt.AskOnFirstRun(["--silent"], () => true, () => { asked = true; return true; }, Registration());

        Assert.False(asked);
        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void The_first_run_of_a_silent_start_asks_nothing_and_says_so_in_the_log()
    {
        StartWithWindowsPrompt.AskOnFirstRun(["--silent"]);

        Assert.Contains("First-run start-with-Windows question skipped", File.ReadAllText(Path.Combine(_home.Path, TrayLog.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_desktop_to_ask_on_it_asks_nothing_and_leaves_it_off()
    {
        var asked = false;

        StartWithWindowsPrompt.AskOnFirstRun([], () => false, () => { asked = true; return true; }, Registration());

        Assert.False(asked);
        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void The_question_is_the_one_the_standard_words()
    {
        Assert.Equal("Start Weir when you sign in to Windows?", StartWithWindowsPrompt.Question);
    }

    [Fact]
    public void An_answer_at_the_first_run_is_recorded_whether_it_is_yes_or_no()
    {
        StartWithWindowsPrompt.AskOnFirstRun([], () => true, () => true, Registration());
        Assert.True(Registration().IsAnswered);

        new StartWithWindowsAnswer(_answer.Path).Forget();
        Assert.False(Registration().IsAnswered);

        StartWithWindowsPrompt.AskOnFirstRun([], () => true, () => false, Registration());
        Assert.True(Registration().IsAnswered);
    }

    [Fact]
    public void A_first_run_that_asks_nothing_records_nothing()
    {
        StartWithWindowsPrompt.AskOnFirstRun(["--silent"], () => true, () => true, Registration());
        StartWithWindowsPrompt.AskOnFirstRun([], () => false, () => true, Registration());

        Assert.False(Registration().IsAnswered);
        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void Ticking_and_unticking_the_menu_item_are_answers()
    {
        Registration().Toggle();
        Assert.True(Registration().IsAnswered);
        Assert.True(Registration().IsEnabled);

        new StartWithWindowsAnswer(_answer.Path).Forget();
        Registration().Toggle();
        Assert.True(Registration().IsAnswered);
        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void An_entry_nobody_chose_is_asked_about_once_and_a_yes_keeps_it()
    {
        Registration().Enable();
        var asked = 0;

        Assert.True(Registration().IsSetWithoutAnswer);
        StartWithWindowsPrompt.AskIfSetWithoutAnswer(true, () => { asked++; return true; }, Registration());
        StartWithWindowsPrompt.AskIfSetWithoutAnswer(true, () => { asked++; return true; }, Registration());

        Assert.Equal(1, asked);
        Assert.True(Registration().IsEnabled);
        Assert.True(Registration().IsAnswered);
        Assert.False(Registration().IsSetWithoutAnswer);
    }

    [Fact]
    public void An_entry_nobody_chose_is_removed_by_a_no_and_not_asked_about_again()
    {
        Registration().Enable();
        var asked = 0;

        StartWithWindowsPrompt.AskIfSetWithoutAnswer(true, () => { asked++; return false; }, Registration());
        StartWithWindowsPrompt.AskIfSetWithoutAnswer(true, () => { asked++; return true; }, Registration());

        Assert.Equal(1, asked);
        Assert.False(Registration().IsEnabled);
        Assert.Null(RunValue());
        Assert.True(Registration().IsAnswered);
    }

    [Fact]
    public void A_start_that_cannot_ask_changes_nothing_and_the_next_one_that_can_asks()
    {
        Registration().Enable();
        var asked = 0;
        bool Ask() { asked++; return true; }

        StartWithWindowsPrompt.AskIfSetWithoutAnswer(false, Ask, Registration());

        Assert.Equal(0, asked);
        Assert.True(Registration().IsEnabled);
        Assert.False(Registration().IsAnswered);

        StartWithWindowsPrompt.AskIfSetWithoutAnswer(true, Ask, Registration());

        Assert.Equal(1, asked);
        Assert.True(Registration().IsAnswered);
    }

    private static bool StartedByPerson(string[] args, bool desktop) =>
        new TrayStart(Program.AnnouncesStart(args), Program.HasInteractiveDesktop(args, () => desktop)).StartedByPerson;

    [Fact]
    public void Only_a_start_the_person_makes_themselves_may_ask()
    {
        Registration().Enable();
        var signInArguments = RunValue()!.Split(' ', 2)[1].Split(' ');

        Assert.True(StartedByPerson([], desktop: true));
        Assert.False(StartedByPerson(["--silent"], desktop: true));
        Assert.False(StartedByPerson([], desktop: false));
        Assert.False(StartedByPerson(["--no-browser"], desktop: true));
        Assert.False(StartedByPerson(signInArguments, desktop: true));
    }

    [Fact]
    public void An_answer_that_could_not_be_carried_out_is_not_recorded_so_the_question_comes_back()
    {
        Registration(executable: null).Choose(startWithWindows: true);

        Assert.False(Registration().IsAnswered);
        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void Forgetting_the_answer_leaves_no_empty_key_behind_and_keeps_a_key_that_holds_something_else()
    {
        Registration().Choose(startWithWindows: true);

        Registration().Uninstall();
        using (var gone = Registry.CurrentUser.OpenSubKey(_answer.Path))
        {
            Assert.Null(gone);
        }

        using (var key = Registry.CurrentUser.CreateSubKey(_answer.Path))
        {
            key.SetValue("Other", 1);
        }

        Registration().Choose(startWithWindows: true);
        Registration().Uninstall();
        using var kept = Registry.CurrentUser.OpenSubKey(_answer.Path);
        Assert.NotNull(kept);
        Assert.Null(kept.GetValue("StartWithWindowsAnswered"));
    }

    [Fact]
    public void A_fresh_install_that_nobody_asked_has_no_entry_and_is_never_asked_about_one()
    {
        var asked = false;

        StartWithWindowsPrompt.AskOnFirstRun(["--silent"], () => true, () => { asked = true; return true; }, Registration());
        StartWithWindowsPrompt.AskIfSetWithoutAnswer(true, () => { asked = true; return true; }, Registration());

        Assert.False(asked);
        Assert.False(Registration().IsEnabled);
        Assert.False(Registration().IsAnswered);
    }

    [Fact]
    public void An_entry_the_person_chose_is_not_asked_about()
    {
        Registration().Choose(startWithWindows: true);
        var asked = false;

        StartWithWindowsPrompt.AskIfSetWithoutAnswer(true, () => { asked = true; return false; }, Registration());

        Assert.False(asked);
        Assert.True(Registration().IsEnabled);
    }

    [Fact]
    public void An_update_neither_answers_the_question_nor_asks_it()
    {
        Registration().Enable();

        Registration().RefreshIfEnabled();

        Assert.True(Registration().IsSetWithoutAnswer);
    }

    [Fact]
    public void Uninstalling_removes_the_entry_and_the_answer_so_a_later_install_asks_again()
    {
        Registration().Choose(startWithWindows: true);

        Registration().Uninstall();

        Assert.False(Registration().IsEnabled);
        Assert.False(Registration().IsAnswered);
    }
}
