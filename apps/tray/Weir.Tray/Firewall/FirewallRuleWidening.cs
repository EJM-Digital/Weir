using System.Runtime.InteropServices;
using Weir.Tray.LanAccess;

namespace Weir.Tray.Firewall;

/// <summary>
/// Widening an older <c>Weir</c> firewall rule that covers fewer network types than the current one (Domain and
/// Private, without Public). On a Private network such a rule still works, so nothing else would ever ask Windows to
/// replace it, and the PC would block other devices the first time it joined a Public network. When the tray
/// starts interactively with network access on, it asks Windows once, through the same administrator prompt as the
/// tray menu. Once means once: <see cref="MarkerFileName"/> is written before the prompt is shown, so quitting
/// during it, a decline and a failure all end the same way, and the person widens the rule themselves by choosing
/// network access again or pressing Try again on System › About.
/// </summary>
static class FirewallRuleWidening
{
    internal const string MarkerFileName = "firewall-rule-widening-asked";

    /// <summary>
    /// Whether a start may ask at all: the saved choice is for other devices, a person is there to answer, and the
    /// question has not been asked before. Cheap, so it is checked before the firewall is read.
    /// </summary>
    internal static bool MayAsk(ListenScope scope, bool interactive, bool alreadyAsked) =>
        scope == ListenScope.OtherDevices && interactive && !alreadyAsked;

    /// <summary>
    /// Whether an allow rule exists and leaves out a profile the current rule covers. No rule at all is not narrow:
    /// creating one is the first-run prompt's job, and the network choice's.
    /// </summary>
    internal static bool IsNarrow(FirewallProfiles allowed) =>
        allowed != FirewallProfiles.None && (allowed & WeirFirewallRule.AllowedProfiles) != WeirFirewallRule.AllowedProfiles;

    /// <summary>Runs <see cref="AskIfNeeded"/> for this PC off the caller's thread: neither the UI nor the server's start waits for the prompt.</summary>
    internal static Task AskInBackground(string runtimeHome, ListenScope scope, bool interactive) =>
        AskInBackground(runtimeHome, scope, interactive, new WindowsFirewallAccess());

    /// <summary>The same with Windows Firewall given; the task ends when the question, if any, has been answered.</summary>
    internal static Task AskInBackground(string runtimeHome, ListenScope scope, bool interactive, IFirewallAccess firewall)
    {
        var work = Task.Run(() => AskIfNeeded(runtimeHome, scope, interactive, ComFirewallPolicy.Open, InstallProcesses.Root(), firewall, TrayLog.Write));
        BackgroundWork.Observe("Firewall rule widening", work);
        return work;
    }

    /// <summary>
    /// Asks Windows to widen the rule when <see cref="MayAsk"/> and <see cref="IsNarrow"/> both say so, and returns
    /// the answer; null when nothing was asked. Blocks until the person has answered the prompt, so it belongs off
    /// the UI thread.
    /// </summary>
    internal static FirewallElevation.Outcome? AskIfNeeded(
        string runtimeHome,
        ListenScope scope,
        bool interactive,
        Func<IFirewallPolicy> openPolicy,
        string installRoot,
        IFirewallAccess firewall,
        Action<string> log)
    {
        if (!MayAsk(scope, interactive, File.Exists(Path.Combine(runtimeHome, MarkerFileName))))
        {
            return null;
        }

        FirewallProfiles allowed;
        try
        {
            allowed = WeirFirewallRule.ProfilesAllowed(openPolicy(), installRoot);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Unreadable is not narrow: a prompt cannot fix a rule nobody can see. Nothing is recorded, so the next
            // start looks again.
            log($"Firewall: Windows Firewall could not be read ({ex.Message}); not checking whether Weir's rule needs widening.");
            return null;
        }
        if (!IsNarrow(allowed))
        {
            return null;
        }

        if (!TryRecordAsked(runtimeHome, log))
        {
            return null;
        }
        log($"Firewall: Weir's rule covers {allowed} only; asking Windows once to widen it to every network type.");
        var outcome = firewall.AskToAllow();
        log(Describe(outcome));
        return outcome;
    }

    // Not asking is safer than asking at every start: without the record nothing could stop that.
    private static bool TryRecordAsked(string runtimeHome, Action<string> log)
    {
        try
        {
            AtomicFile.WriteAllText(runtimeHome, MarkerFileName, "asked");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Firewall: Weir's rule needs widening, but the question could not be recorded ({ex.Message}), so it is not asked.");
            return false;
        }
    }

    private static string Describe(FirewallElevation.Outcome outcome) => outcome switch
    {
        FirewallElevation.Outcome.Configured => "Firewall: Configured. Weir's rule now covers every network type.",
        FirewallElevation.Outcome.Declined => "Firewall: Declined. Weir's rule stays as it is and Weir will not ask again by itself; choosing network access again, or Try again on System › About, widens it.",
        _ => "Firewall: Failed. Weir's rule stays as it is and Weir will not ask again by itself; choosing network access again, or Try again on System › About, widens it.",
    };
}
