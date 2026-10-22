using System.Diagnostics.CodeAnalysis;

namespace Weir.Tray;

/// <summary>
/// The question "Start Weir when you sign in to Windows?". Nothing starts with Windows unless the person says yes here or
/// later ticks "Start with Windows" in the tray menu. It is asked at first run, and once more for an entry that an older
/// version set at install without asking. A silent start, or one with no desktop to ask on, asks nothing and changes nothing.
/// </summary>
static class StartWithWindowsPrompt
{
    internal const string Question = "Start Weir when you sign in to Windows?";

    internal static void AskOnFirstRun(string[] args) =>
        AskOnFirstRun(args, PortChoice.HasInteractiveDesktop, AskInMessageBox, StartupRegistration.ForThisUser());

    internal static void AskOnFirstRun(IEnumerable<string> args, Func<bool> desktopCheck, Func<bool> ask, StartupRegistration startup)
    {
        if (!Program.HasInteractiveDesktop(args, desktopCheck))
        {
            TrayLog.Write("First-run start-with-Windows question skipped: silent start or no interactive desktop. Weir does not start with Windows.");
            return;
        }
        startup.Choose(ask());
    }

    /// <summary>
    /// Asks, once, about an entry nobody chose. Versions up to 1.0.0-rc.10 registered it at install without asking, and the
    /// standard needs a Yes from the person, so an entry with no recorded answer is asked about like any other first time. Only
    /// a start the person made themselves qualifies (<see cref="TrayStart.StartedByPerson"/>): not a silent one, one with no
    /// desktop, or one with <see cref="Program.NoBrowserArgument"/>, which is how the entry itself starts Weir at sign-in (and
    /// how an update restart and Deluno start it), none of which should put a question on the screen. A start that does not
    /// qualify changes nothing; the next one that does asks. The tray calls this once its icon is up.
    /// </summary>
    internal static void AskIfSetWithoutAnswer(bool startedByPerson, Func<bool> ask, StartupRegistration startup)
    {
        if (!startup.IsSetWithoutAnswer)
        {
            return;
        }
        if (!startedByPerson)
        {
            TrayLog.Write("Start-with-Windows question for an entry nobody chose is waiting for a start the person makes themselves.");
            return;
        }
        TrayLog.Write("Weir starts with Windows, but nobody was asked: asking now.");
        startup.Choose(ask());
    }

    [ExcludeFromCodeCoverage]
    internal static bool AskInMessageBox() =>
        MessageBox.Show(Question, "Weir", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
}
