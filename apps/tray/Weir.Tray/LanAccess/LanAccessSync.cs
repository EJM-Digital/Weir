using Weir.Tray.Firewall;

namespace Weir.Tray.LanAccess;

/// <summary>The part of the running server that <see cref="LanAccessSync"/> steers.</summary>
interface IServerListenScope
{
    ListenScope Scope { get; }

    /// <summary>Restarts the server for <paramref name="to"/>; goes back to the old scope if it does not come up.</summary>
    Task<ScopeChange> MoveToScopeAsync(ListenScope to, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the running server listening the way the saved LAN access choice says. The tray's own menu goes through
/// <see cref="SetAsync"/>; a change made by another process (the web page, <c>--allow-lan</c>) is picked up by
/// <see cref="WatchAsync"/>, which restarts the server to match. The saved choice and the running server never
/// disagree for long: a server that will not start the new way puts the saved choice back.
/// </summary>
/// <remarks>
/// A choice for other devices that arrives while Windows Firewall has no rule for Weir raises the same administrator
/// prompt the tray menu does. Declining it still restarts the server for the network, because that is what was
/// chosen; Windows Firewall then blocks, which System › About shows with a way to try again. Saving the choice again
/// (the file's modified time moves even when its text does not) asks again. Once Windows has answered, the tray saves
/// the choice again, so the server that watches the file can tell System › About the firewall's new state.
/// </remarks>
sealed class LanAccessSync(string runtimeHome, IServerListenScope server, IFirewallAccess firewall, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    // Held while a choice is saved and applied, or while the saved choice is checked against the server, so the
    // menu and the watcher never both restart the server, or raise the admin prompt, for the same change.
    private readonly SemaphoreSlim _changing = new(1, 1);

    // When the saved choice was last written by a process the tray has already dealt with. A start with a choice
    // already saved never raises the prompt: only a save made while the tray is running does.
    private DateTime? _handledSavedAt = LanAccessSetting.SavedAt(runtimeHome);

    public void Dispose() => _changing.Dispose();

    /// <summary>Saves <paramref name="scope"/> and restarts the server to match.</summary>
    internal async Task<ScopeChange> SetAsync(ListenScope scope, CancellationToken cancellationToken)
    {
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LanAccessSetting.Write(runtimeHome, scope);
            _handledSavedAt = LanAccessSetting.SavedAt(runtimeHome);
            return await ApplyAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>
    /// Carries out every choice another process saves: restarts the server for it, and reports each one to
    /// <paramref name="watch"/>.
    /// </summary>
    internal async Task WatchAsync(LanAccessWatch watch, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval, time);
        while (true)
        {
            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            await CheckAsync(watch, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task CheckAsync(LanAccessWatch watch, CancellationToken cancellationToken)
    {
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A file that is being replaced says nothing: not a choice, not a change, and not a reason to put the server back.
            var saved = LanAccessSetting.Look(runtimeHome, log: _ => { });
            if (saved.Busy)
            {
                return;
            }

            var isNewSave = saved.SavedAt != _handledSavedAt;
            _handledSavedAt = saved.SavedAt;
            if (saved.Scope is not { } scope)
            {
                return;
            }

            var asked = isNewSave && scope == ListenScope.OtherDevices && !firewall.AllowsWeirIn()
                ? await AskWindowsAsync(watch, cancellationToken).ConfigureAwait(false)
                : (FirewallElevation.Outcome?)null;
            if (asked is not null)
            {
                SaveAgain(scope);
            }

            var change = scope == server.Scope
                ? ScopeChange.Unchanged
                : await ApplyAsync(scope, cancellationToken).ConfigureAwait(false);
            if (change != ScopeChange.Unchanged || asked is not null)
            {
                watch.Applied(new SavedChoiceApplied(scope, change, asked));
            }
        }
        finally
        {
            _changing.Release();
        }
    }

    // The prompt blocks until the person answers it, off the caller's thread.
    private async Task<FirewallElevation.Outcome> AskWindowsAsync(LanAccessWatch watch, CancellationToken cancellationToken)
    {
        TrayLog.Write("LAN access: a choice for other devices was saved and Windows Firewall has no rule for Weir; asking Windows for it.");
        watch.AskingWindows();
        return await Task.Run(firewall.AskToAllow, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScopeChange> ApplyAsync(ListenScope scope, CancellationToken cancellationToken)
    {
        var change = await server.MoveToScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        if (change == ScopeChange.Failed)
        {
            PutSavedChoiceBack();
        }
        return change;
    }

    private void PutSavedChoiceBack() => Save(server.Scope, "the saved choice could not be put back");

    // The server watches the saved choice and tells System › About when it is written, so saving the same choice once
    // Windows has answered is how the page learns the firewall's new state when the server is not restarted for it.
    private void SaveAgain(ListenScope scope) => Save(scope, "the saved choice could not be saved again after Windows answered");

    private void Save(ListenScope scope, string failure)
    {
        try
        {
            LanAccessSetting.Write(runtimeHome, scope);
            _handledSavedAt = LanAccessSetting.SavedAt(runtimeHome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Write($"LAN access: {failure} ({ex.Message}).");
        }
    }
}
