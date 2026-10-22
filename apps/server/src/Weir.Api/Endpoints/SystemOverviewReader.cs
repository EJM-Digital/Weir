using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Metrics;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>GET /api/v1/system/overview</c> body: the facts System shows about this copy of Weir, each read from the store that
/// already holds it. Nothing here asks a media manager, a download client or GitHub; the slow ones (the size of the data folder,
/// the latest release) are kept and refreshed on their own.
/// </summary>
internal sealed class SystemOverviewReader
{
    /// <summary>How far back "restarts this week" looks.</summary>
    private static readonly TimeSpan RestartWindow = TimeSpan.FromDays(7);

    private readonly ServerLifecycle _lifecycle;
    private readonly ServerListenOptions _listen;
    private readonly MachineIdentity _machine;
    private readonly ServerRunMode _runMode;
    private readonly TimeProvider _time;
    private readonly ActivityStreamClients _streamClients;
    private readonly RuntimeMetricsStore _metrics;
    private readonly JobsInspectionStore _jobs;
    private readonly ServerStartStore _starts;
    private readonly DataFootprint _footprint;
    private readonly UpdateOutlook _updates;
    private readonly SuiteSettingsStore _suiteSettings;
    private readonly SystemChecks _checks;
    private readonly WeirOptions _options;

    public SystemOverviewReader(
        ServerLifecycle lifecycle,
        ServerListenOptions listen,
        MachineIdentity machine,
        ServerRunMode runMode,
        TimeProvider time,
        ActivityStreamClients streamClients,
        RuntimeMetricsStore metrics,
        JobsInspectionStore jobs,
        ServerStartStore starts,
        DataFootprint footprint,
        UpdateOutlook updates,
        SuiteSettingsStore suiteSettings,
        SystemChecks checks,
        WeirOptions options)
    {
        _lifecycle = lifecycle;
        _listen = listen;
        _machine = machine;
        _runMode = runMode;
        _time = time;
        _streamClients = streamClients;
        _metrics = metrics;
        _jobs = jobs;
        _starts = starts;
        _footprint = footprint;
        _updates = updates;
        _suiteSettings = suiteSettings;
        _checks = checks;
        _options = options;
    }

    public async Task<WireObject> ReadAsync(UnitOfWork uow, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(services);
        var now = _time.GetUtcNow();
        var suite = await _suiteSettings.GetAsync(uow).ConfigureAwait(false);
        var today = TimeZones.StartOfLocalDay(now, suite?.AppTimezone);
        var readiness = await SystemEndpoints.BuildReadinessAsync(services).ConfigureAwait(false);
        var checks = await _checks.CountAsync(uow, readiness).ConfigureAwait(false);
        var jobsToday = await _jobs.CountFinishedSinceAsync(uow, today).ConfigureAwait(false);
        var restarts = await _starts.RestartsSinceAsync(uow, now - RestartWindow).ConfigureAwait(false);
        var requests = _metrics.GetRequestFigures(today);
        var update = _updates.Current();
        var dataBytes = await _footprint.BytesAsync(cancellationToken).ConfigureAwait(false);

        return new WireObject()
            .Set("version", readiness.Version)
            .Set("update", new WireObject()
                .Set("status", update.Status)
                .Set("latest_version", update.LatestVersion))
            .Set("uptime_seconds", (long)_lifecycle.Elapsed.TotalSeconds)
            .Set("started_at", Timestamp.FromDateTimeOffset(_lifecycle.StartedAt.ToUniversalTime()).ToWireText())
            .Set("runs_as", _runMode.Name)
            .Set("address", AddressOf(_listen, _machine))
            .Set("data_bytes", dataBytes)
            .Set("browsers_live", _streamClients.Count)
            .Set("requests", new WireObject()
                .Set("median_ms", requests.MedianMs)
                .Set("p95_ms", requests.P95Ms)
                .Set("errors_today", requests.ServerErrors))
            .Set("jobs_today", new WireObject().Set("run", jobsToday.Run).Set("failed", jobsToday.Failed))
            .Set("restarts_this_week", restarts)
            .Set("checks", new WireObject().Set("passing", checks.Passing).Set("total", checks.Total))
            .Set("last_update_backup", UpdateBackupOut(PreUpdateBackup.Latest(_options.BackupDir)));
    }

    private WireValue UpdateBackupOut(PreUpdateBackupFile? backup) => backup is null
        ? WireNull.Instance
        : new WireObject()
            .Set("path", backup.DatabasePath)
            .Set("taken_at", Timestamp.FromDateTimeOffset(backup.TakenAt).ToWireText())
            .Set("from_version", backup.FromVersion)
            .Set("to_version", backup.ToVersion)
            .Set("in_data_folder", IsInDataFolder(backup.DatabasePath));

    private bool IsInDataFolder(string path)
    {
        var relative = Path.GetRelativePath(_options.WeirHome, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// Where a browser reaches Weir: this PC's own name when it listens on every interface, <c>localhost</c> when only this PC
    /// can connect, otherwise the address it was told to bind.
    /// </summary>
    internal static string AddressOf(ServerListenOptions listen, MachineIdentity machine)
    {
        var host = listen.Host switch
        {
            "0.0.0.0" or "*" or "::" => machine.Name,
            _ when listen.IsThisPcOnly => "localhost",
            _ => listen.Host,
        };
        return host.Contains(':', StringComparison.Ordinal) ? $"http://[{host}]:{listen.Port}" : $"http://{host}:{listen.Port}";
    }
}
