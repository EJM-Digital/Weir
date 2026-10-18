using System.Text.Json.Nodes;

namespace Weir.E2E.Tests.Support;

/// <summary>The body the Workflows editor sends, as a test states it.</summary>
public static class WorkflowBodies
{
    /// <summary>What Weir sends to create a Weir-only movie workflow named <paramref name="name"/>, with nothing else filled in.</summary>
    public static JsonObject Default(string name) => new()
    {
        ["name"] = name,
        ["media_type"] = "movie",
        ["enabled"] = true,
        ["watched_folder"] = string.Empty,
        ["work_folder"] = string.Empty,
        ["output_folder"] = string.Empty,
        ["media_extensions_csv"] = ".mkv,.mp4,.m4v,.webm,.avi",
        ["exclude_markers_csv"] = ".sabnzbd,__admin__,_failed_,_unpack_,_repair_,incomplete",
        ["include_patterns_csv"] = string.Empty,
        ["exclude_patterns_csv"] = string.Empty,
        ["min_file_size_mb"] = 50,
        ["max_file_size_mb"] = 0,
        ["rejected_file_action"] = "leave",
        ["ready_after_seconds"] = 60,
        ["created_after"] = null,
        ["created_before"] = null,
        ["modified_after"] = null,
        ["modified_before"] = null,
        ["scan_interval_seconds"] = 300,
        ["max_concurrent_files"] = 0,
        ["priority"] = 0,
        ["sidecar_patterns_csv"] = ".srt,.ass,.ssa,.sub,.idx,.vtt,.nfo,.jpg,.png",
        ["preserve_original_timestamps"] = false,
        ["remove_original_after_success"] = true,
        ["minimum_free_disk_space_mb"] = 5120,
        ["output_collision_policy"] = "replace",
        ["ffmpeg_strictness"] = "normal",
        ["max_attempts"] = 3,
        ["retry_backoff_seconds"] = 300,
        ["retry_execution_failures"] = true,
        ["retry_preflight_failures"] = false,
        ["failure_policy"] = "pass_through",
        ["exclude_hidden"] = true,
        ["top_level_only"] = false,
        ["ignore_size_changes"] = false,
        ["skip_access_tests"] = false,
        ["file_system_events_enabled"] = true,
        ["schedule_grid"] = string.Empty,
        ["schedule_enabled"] = true,
        ["schedule_hours_limited"] = false,
        ["schedule_days"] = string.Empty,
        ["schedule_start"] = "00:00",
        ["schedule_end"] = "23:59",
        ["rule_set_id"] = null,
        ["manager_connection_ids"] = new JsonArray(),
        ["remux_writer"] = "best",
    };
}
