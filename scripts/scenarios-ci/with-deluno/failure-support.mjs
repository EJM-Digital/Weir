// What the scenarios that make a hand-off go wrong share: Weir's report to Deluno as the recording proxy saw it, a workflow setting changed and put
// back, and which log rows such a scenario expects.
import { wait } from "../workflow-support.mjs";
import { ANSWER_CEILING_MS } from "./handoffs.mjs";

const EVENTS_PATH = "/api/integrations/processors/events";

/** The settings a workflow is saved with (the others it reads back are Weir's own bookkeeping, and a save that names them is refused). */
export const WORKFLOW_SETTINGS = [
  "name", "media_type", "enabled", "watched_folder", "work_folder", "output_folder", "media_extensions_csv", "exclude_markers_csv", "include_patterns_csv",
  "exclude_patterns_csv", "min_file_size_mb", "max_file_size_mb", "rejected_file_action", "ready_after_seconds", "created_after", "created_before",
  "modified_after", "modified_before", "exclude_hidden", "top_level_only", "sidecar_patterns_csv", "preserve_original_timestamps", "output_collision_policy",
  "ffmpeg_strictness", "remux_writer", "scan_interval_seconds", "ignore_size_changes", "skip_access_tests", "max_attempts", "retry_backoff_seconds",
  "retry_execution_failures", "failure_policy", "retry_preflight_failures", "schedule_grid", "file_system_events_enabled", "schedule_enabled",
  "schedule_hours_limited", "schedule_days", "schedule_start", "schedule_end", "max_concurrent_files", "priority", "rule_set_id", "manager_connection_ids",
  "remove_original_after_success", "minimum_free_disk_space_mb",
];

/** The body that saves a workflow as it is with some settings changed. */
export function workflowSave(workflow, changes) {
  const kept = Object.fromEntries(WORKFLOW_SETTINGS.filter((name) => name in workflow).map((name) => [name, workflow[name]]));
  return { ...kept, ...changes };
}

/** Saves the workflow with these settings changed, and returns what Weir now shows. */
export async function changeWorkflow(world, workflow, changes) {
  const saved = await world.weir.write("PUT", `/processing/libraries/${workflow.id}`, workflowSave(workflow, changes));
  if (saved.status !== 200) throw new Error(`Weir would not save '${workflow.name}' with ${JSON.stringify(changes)} (answered ${saved.status}: ${saved.text.slice(0, 400)})`);
  return saved.json;
}

/** The reports Weir sent Deluno about a hand-off, oldest first, each as the proxy recorded it with Deluno's reply. */
export async function reportsAbout(world, handoff) {
  return world.deluno.exchanges.where("to-deluno", (exchange) => exchange.request.method === "POST" && exchange.request.path.split("?")[0] === EVENTS_PATH && exchange.request.body?.handoffId === handoff.id);
}

/** Waits for a report about the hand-off that `accepted` takes, and returns it. */
export function waitForReport(world, handoff, accepted, what) {
  return wait(
    world,
    async () => (await reportsAbout(world, handoff)).find((exchange) => accepted(exchange.request.body)),
    what,
    ANSWER_CEILING_MS,
    2000,
  );
}

/**
 * Notes that a film was made to fail. A scenario that makes Weir fail on purpose expects the warnings that say so, and Weir looks again at a file
 * it could not read for as long as the stack runs, so a later scenario sees them too.
 */
export function failsOnPurpose(world, film) {
  (world.failingFilms ??= []).push(film);
  return film;
}

/** Whether a log row is about one of these films. Any other warning or error is not expected. */
export const rowAbout = (films) => (row) => films.some((film) => JSON.stringify(row).includes(film.title));
