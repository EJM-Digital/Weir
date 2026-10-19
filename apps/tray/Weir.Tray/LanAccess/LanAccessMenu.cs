using Weir.Tray.Firewall;

namespace Weir.Tray.LanAccess;

/// <summary>
/// What the tray's two LAN access items, "Allow other devices on your network..." and "Only allow this PC", do, and what
/// they say while it happens. The tray draws <see cref="State"/> and calls <see cref="Allow"/> and
/// <see cref="LimitToThisPc"/> from the items' clicks. Everything here runs on the UI thread: the menu handlers start it
/// there, and the work that leaves it (the Windows admin prompt, the server restart) hands back to it when it finishes.
/// </summary>
sealed class LanAccessMenu
{
    private readonly LanAccessSync _sync;
    private readonly IServerListenScope _server;
    private readonly IFirewallAccess _firewall;
    private readonly Action<LanAccessNotice> _notify;
    private readonly Action _changed;
    private readonly CancellationToken _cancellationToken;
    private LanAccessActivity _activity = LanAccessActivity.Idle;

    // notify tells the person how a change went; changed is called when State changes, so the tray draws it again.
    internal LanAccessMenu(LanAccessSync sync, IServerListenScope server, IFirewallAccess firewall, Action<LanAccessNotice> notify, Action changed, CancellationToken cancellationToken)
    {
        _sync = sync;
        _server = server;
        _firewall = firewall;
        _notify = notify;
        _changed = changed;
        _cancellationToken = cancellationToken;
    }

    /// <summary>What the two items say and whether they can be clicked, now.</summary>
    internal LanAccessMenuState State => LanAccessMenuState.Describe(_server.Scope, _activity);

    internal void Allow() => BackgroundWork.Observe("Allow other devices on your network", AllowAsync());

    internal void LimitToThisPc() => BackgroundWork.Observe("Only allow this PC", LimitToThisPcAsync());

    /// <summary>Another process (the web page, <c>--allow-lan</c>) saved a choice that needs the Windows admin prompt.</summary>
    internal void OnWaitingForWindows() => Show(LanAccessActivity.WaitingForWindows);

    /// <summary>Another process changed the choice and the tray has carried it out, or could not.</summary>
    internal void OnChangedElsewhere(SavedChoiceApplied applied)
    {
        Show(LanAccessActivity.Idle);
        _notify(LanAccessNotice.ForSavedChoice(applied));
    }

    // Blocks on the elevated child process (and the UAC prompt the person answers) off the UI thread, so the tray's
    // message loop never freezes on it.
    internal async Task AllowAsync()
    {
        TrayLog.Write("Allow other devices on your network: requested from the tray menu.");
        Show(LanAccessActivity.WaitingForWindows);
        try
        {
            var outcome = await Task.Run(_firewall.AskToAllow, _cancellationToken);
            if (outcome != FirewallElevation.Outcome.Configured)
            {
                _notify(LanAccessNotice.FirewallStepFailed(outcome, _server.Scope));
                return;
            }
            Show(LanAccessActivity.RestartingForOtherDevices);
            var change = await _sync.SetAsync(ListenScope.OtherDevices, _cancellationToken);
            _notify(change == ScopeChange.Unchanged
                ? LanAccessNotice.FirewallAllowsWeir()
                : LanAccessNotice.Restarted(ListenScope.OtherDevices, change));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"LAN access: the choice could not be saved ({ex.Message}).");
            _notify(LanAccessNotice.NotSaved());
        }
        catch (OperationCanceledException)
        {
            TrayLog.Write("Allow other devices on your network: stopped, because Weir is quitting or updating.");
        }
        finally
        {
            Show(LanAccessActivity.Idle);
        }
    }

    internal async Task LimitToThisPcAsync()
    {
        TrayLog.Write("Only allow this PC: requested from the tray menu.");
        Show(LanAccessActivity.RestartingForThisPcOnly);
        try
        {
            var change = await _sync.SetAsync(ListenScope.ThisPcOnly, _cancellationToken);
            _notify(LanAccessNotice.Restarted(ListenScope.ThisPcOnly, change));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"LAN access: the choice could not be saved ({ex.Message}).");
            _notify(LanAccessNotice.NotSaved());
        }
        catch (OperationCanceledException)
        {
            TrayLog.Write("Only allow this PC: stopped, because Weir is quitting or updating.");
        }
        finally
        {
            Show(LanAccessActivity.Idle);
        }
    }

    private void Show(LanAccessActivity activity)
    {
        _activity = activity;
        _changed();
    }
}
