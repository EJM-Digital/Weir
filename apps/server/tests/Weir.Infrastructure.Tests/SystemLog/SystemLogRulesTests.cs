using System.Reflection;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.Logs;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.SystemLog;

namespace Weir.Infrastructure.Tests.SystemLog;

/// <summary>How a row of System › Logs gets its level and its category.</summary>
public sealed class SystemLogRulesTests
{
    [Theory]
    [InlineData("auth.login_failed", SystemLogCategories.SignIn)]
    [InlineData("auth.password_changed", SystemLogCategories.SignIn)]
    [InlineData("arr_library.connection_test_failed", SystemLogCategories.Connections)]
    [InlineData("system.reconciliation.repair", SystemLogCategories.Connections)]
    [InlineData("system.network_access_changed", SystemLogCategories.Weir)]
    [InlineData("library.scan_completed", SystemLogCategories.Scans)]
    [InlineData("library.file_cleaned", SystemLogCategories.Library)]
    [InlineData("library.original_keep_conflict", SystemLogCategories.Library)]
    [InlineData("library.redownload_requested", SystemLogCategories.Library)]
    [InlineData("library.file_change_notified", SystemLogCategories.Connections)]
    [InlineData("library.file_change_notify_warning", SystemLogCategories.Connections)]
    [InlineData("processing.file_remux_pass_completed", SystemLogCategories.Processing)]
    [InlineData("processing.worker_failure", SystemLogCategories.Processing)]
    [InlineData("processing.handoff_reported", SystemLogCategories.Connections)]
    [InlineData("processing.handback_outcome", SystemLogCategories.Connections)]
    [InlineData("processing.downloaded_scan_requested", SystemLogCategories.Connections)]
    [InlineData("processing.unclaimed_handback_cleanup_completed", SystemLogCategories.Cleanup)]
    [InlineData("processing.work_temp_stale_sweep_completed", SystemLogCategories.Cleanup)]
    [InlineData("processing.failure_cleanup_sweep_completed", SystemLogCategories.Cleanup)]
    [InlineData("processing.file_removal_kept", SystemLogCategories.Cleanup)]
    [InlineData("processing.file_left_watched_folder", SystemLogCategories.Scans)]
    [InlineData("something.new", SystemLogCategories.Weir)]
    public void An_event_type_belongs_to_the_category_its_family_is_about(string eventType, string category)
    {
        Assert.Equal(category, SystemLogRules.EventCategory(eventType));
    }

    [Fact]
    public void Every_event_type_the_server_defines_has_a_category_the_picker_offers()
    {
        var eventTypes = ConstantsOf(typeof(ActivityEventTypes)).Concat(ConstantsOf(typeof(LibraryActivityEventTypes))).ToList();

        Assert.NotEmpty(eventTypes);
        Assert.All(eventTypes, eventType => Assert.Contains(SystemLogRules.EventCategory(eventType), SystemLogCategories.All));
    }

    [Fact]
    public void Every_category_is_one_the_picker_can_choose_and_none_is_unreachable()
    {
        var reached = SystemLogRules.EventTypeCategories.Select(rule => rule.Category)
            .Concat(SystemLogRules.JobKinds.Select(rule => rule.Category))
            .Concat([SystemLogRules.ServerCategory("weir.platform.suite_settings.backups"), SystemLogRules.ServerCategory("weir.platform.suite_settings.update_service")])
            .ToHashSet();

        Assert.All(reached, category => Assert.Contains(category, SystemLogCategories.All));
        Assert.Contains(SystemLogCategories.Backups, reached);
        Assert.Contains(SystemLogCategories.Updates, reached);
    }

    [Theory]
    [InlineData("failed", SystemLogLevels.Error)]
    [InlineData("warning", SystemLogLevels.Warning)]
    [InlineData("retrying", SystemLogLevels.Warning)]
    [InlineData("success", SystemLogLevels.Success)]
    [InlineData("skipped", SystemLogLevels.Info)]
    [InlineData("running", SystemLogLevels.Info)]
    [InlineData(null, SystemLogLevels.Info)]
    public void An_events_result_reads_as_a_level(string? result, string level)
    {
        Assert.Equal(level, SystemLogRules.EventLevel(result));
    }

    [Theory]
    [InlineData(ProcessingJobStatus.Failed, null, SystemLogLevels.Error)]
    [InlineData(ProcessingJobStatus.HandlerOkFinalizeFailed, null, SystemLogLevels.Warning)]
    [InlineData(ProcessingJobStatus.Pending, "The file was still being written", SystemLogLevels.Warning)]
    [InlineData(ProcessingJobStatus.Pending, null, SystemLogLevels.Info)]
    [InlineData(ProcessingJobStatus.Pending, "", SystemLogLevels.Info)]
    [InlineData(ProcessingJobStatus.Leased, null, SystemLogLevels.Info)]
    [InlineData(ProcessingJobStatus.Completed, null, SystemLogLevels.Success)]
    [InlineData(ProcessingJobStatus.Cancelled, null, SystemLogLevels.Info)]
    public void A_jobs_status_reads_as_a_level(string status, string? lastError, string level)
    {
        Assert.Equal(level, SystemLogRules.JobLevel(status, lastError));
    }

    [Theory]
    [InlineData("ERROR", SystemLogLevels.Error)]
    [InlineData("CRITICAL", SystemLogLevels.Error)]
    [InlineData("warning", SystemLogLevels.Warning)]
    [InlineData("INFO", SystemLogLevels.Info)]
    [InlineData("DEBUG", SystemLogLevels.Info)]
    public void A_log_lines_level_reads_as_a_level(string level, string expected)
    {
        Assert.Equal(expected, SystemLogRules.ServerLevel(level));
    }

    [Theory]
    [InlineData("weir.platform.suite_settings.backups", SystemLogCategories.Backups)]
    [InlineData("Weir.Infrastructure.Settings.ConfigurationBackupTask", SystemLogCategories.Backups)]
    [InlineData("weir.platform.suite_settings.update_service", SystemLogCategories.Updates)]
    [InlineData("weir.platform.auth.router", SystemLogCategories.SignIn)]
    [InlineData("weir.platform.auth.session_cleanup", SystemLogCategories.SignIn)]
    [InlineData("Weir.Infrastructure.Jobs.UnclaimedHandbackCleanupHandler", SystemLogCategories.Cleanup)]
    [InlineData("Weir.Infrastructure.Jobs.WatchedFolderScanBatch", SystemLogCategories.Scans)]
    [InlineData("weir.connection_peer", SystemLogCategories.Connections)]
    [InlineData("Weir.Infrastructure.MediaManagers.HandoffCompletionReporter", SystemLogCategories.Connections)]
    [InlineData("weir.library_mode.router", SystemLogCategories.Library)]
    [InlineData("weir.processing.rules_preview", SystemLogCategories.Processing)]
    [InlineData("Weir.Infrastructure.Jobs.ProcessingJobProcessor", SystemLogCategories.Processing)]
    [InlineData("weir.platform.http.request_context", SystemLogCategories.Weir)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", SystemLogCategories.Weir)]
    public void A_logger_belongs_to_the_part_of_weir_it_names(string logger, string category)
    {
        Assert.Equal(category, SystemLogRules.ServerCategory(logger));
    }

    [Fact]
    public void Every_job_kind_weir_runs_has_a_label_and_a_category()
    {
        var kinds = new[]
        {
            LibraryModeJobKinds.ScanKind, LibraryModeJobKinds.CleanKind, PeriodicJobKinds.WorkTempStaleSweep, PeriodicJobKinds.UnclaimedHandbackCleanup,
            DownloadedScanRetryHandler.Kind,
        };

        Assert.All(kinds, kind => Assert.NotEqual(SystemLogCategories.Weir, SystemLogRules.JobCategory(kind)));
        Assert.All(SystemLogRules.JobKinds, rule => Assert.False(string.IsNullOrWhiteSpace(rule.Label)));
        Assert.Equal(SystemLogRules.JobKinds.Count, SystemLogRules.JobKinds.Select(rule => rule.Kind).Distinct().Count());
    }

    [Theory]
    [InlineData("processing.file.remux_pass.v1", "Process a media file")]
    [InlineData("processing.watched_folder.remux_scan_dispatch.v1", "Check watched folders")]
    [InlineData("processing.something_new.v2", "Something new")]
    [InlineData("oddkind", "Oddkind")]
    public void A_job_kind_reads_in_words(string kind, string label)
    {
        Assert.Equal(label, SystemLogRules.JobKindLabel(kind));
    }

    [Fact]
    public void Every_job_status_has_a_word()
    {
        var statuses = new[]
        {
            ProcessingJobStatus.Pending, ProcessingJobStatus.Leased, ProcessingJobStatus.Completed, ProcessingJobStatus.Failed,
            ProcessingJobStatus.Cancelled, ProcessingJobStatus.HandlerOkFinalizeFailed,
        };

        Assert.All(statuses, status => Assert.NotEqual(status, SystemLogRules.JobStatusLabel(status)));
    }

    private static IEnumerable<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    [Fact]
    public void A_log_with_no_conditions_has_no_where_clause()
    {
        Assert.Equal(string.Empty, new SystemLogConditions().WhereText);
        Assert.Equal(" WHERE a = 1 AND b = 2", new SystemLogConditions(["a = 1", "b = 2"]).WhereText);
    }

    [Fact]
    public void A_job_kind_with_no_name_reads_as_itself()
    {
        Assert.Equal(string.Empty, SystemLogRules.JobKindLabel(string.Empty));
    }
}
