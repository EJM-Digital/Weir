using System.Globalization;

namespace Weir.Tray;

/// <summary>
/// Decides when an Automatic-mode update may install itself: once the server has said it is idle, without a break,
/// for <see cref="IdlePeriod"/> (#875). It only decides; installing is the caller's job, through the same
/// restart-to-update path a person uses, which stops the server cleanly first.
/// </summary>
sealed class IdleInstall(string runtimeHome, TimeProvider clock)
{
    /// <summary>How long the server must stay idle before an update installs.</summary>
    internal static readonly TimeSpan IdlePeriod = TimeSpan.FromMinutes(5);

    /// <summary>How often the server's answer is read. It is the server's own refresh rate, so no answer goes unread.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>The idle period as the notices and the log say it: "5 minutes".</summary>
    internal static string IdlePeriodText { get; } = string.Create(CultureInfo.InvariantCulture, $"{IdlePeriod.TotalMinutes:0} minutes");

    private readonly IdleCountdown _countdown = new(IdlePeriod);
    private ServerWork? _lastAnswer;

    /// <summary>Returns once the server has been idle for <see cref="IdlePeriod"/> in a row, or throws when cancelled.</summary>
    internal async Task WaitUntilIdleAsync(CancellationToken cancellationToken)
    {
        while (!Poll())
        {
            await Task.Delay(PollInterval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the server's answer once. True when it has now been idle for the whole period.</summary>
    internal bool Poll()
    {
        var now = clock.GetUtcNow();
        var work = WorkStateFile.Read(runtimeHome, now);
        LogChange(work);
        return _countdown.Observe(work, now);
    }

    // Only a change is logged: a wait of hours would otherwise write a line every few seconds.
    private void LogChange(ServerWork work)
    {
        if (work == ServerWork.NotRead || work == _lastAnswer)
        {
            return;
        }
        _lastAnswer = work;
        TrayLog.Write(work switch
        {
            ServerWork.Idle => $"Weir is idle. The update installs if it stays idle for {IdlePeriodText}.",
            ServerWork.Busy => "Weir has work to do. The update waits for it to be idle.",
            _ => "Weir has not said whether it is idle. The update waits.",
        });
    }
}
