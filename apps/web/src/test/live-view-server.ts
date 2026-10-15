import type { ActivityEventItem, SystemReadiness } from "../lib/api/types";
import type { ProcessingJobsInspectionOut } from "../lib/processing/jobs-inspection/types";
import type { AppSettings } from "../lib/settings/types";
import type { ProcessingFile } from "../lib/processing/files-api";
import type { ProcessingLibrary } from "../lib/processing/libraries-api";
import type { LibraryOverview } from "../lib/processing/library-mode-api";
import type { LibraryFolderChain } from "../lib/processing/library-folder-chain-api";
import type {
  ProcessingFilesAtOnceOut,
  ProcessingOverviewStatsOut,
} from "../lib/processing/types";
import {
  activityPage,
  filesPage,
  readyServer,
  workflowRow,
} from "./dashboard-fixtures";
import type { StubbedRequest } from "./stub-weir-api";

/** What the server holds for the Dashboard's Live view; a test changes it and then makes the stream say so. */
export type LiveWorld = {
  workflows: ProcessingLibrary[];
  files: ProcessingFile[];
  stats: ProcessingOverviewStatsOut;
  filesAtOnce: ProcessingFilesAtOnceOut;
  /** Finished passes of downloads, newest first. */
  passes: ActivityEventItem[];
  /** Library files cleaned, newest first. */
  cleans: ActivityEventItem[];
};

const PASS_EVENT = "processing.file_remux_pass_completed";
const CLEAN_EVENT = "library.file_cleaned";

/** A quiet Weir with one workflow and nothing happening. */
export function quietWorld(): LiveWorld {
  return {
    workflows: [workflowRow({ id: 1, name: "Movies" })],
    files: [],
    stats: {
      already_optimized_count: 0,
      files_failed: 0,
      files_processed: 0,
      net_space_saved_bytes: 0,
      net_space_saved_percent: 0,
      output_written_count: 0,
      success_rate_percent: 0,
      window_days: 1,
    },
    filesAtOnce: {
      files_at_once: 2,
      worker_slots: 2,
      effective_files_at_once: 2,
      running: 0,
      waiting: 0,
      waiting_for: "nothing",
      message: "Nothing is waiting.",
      slots_note: "",
    },
    passes: [],
    cleans: [],
  };
}

function filesOf(world: LiveWorld, request: StubbedRequest) {
  const statuses = request.query.get("file_status")?.split(",");
  const matching = statuses
    ? world.files.filter((file) => statuses.includes(file.status))
    : world.files;
  return filesPage(matching);
}

function activityOf(world: LiveWorld, request: StubbedRequest) {
  const type = request.query.get("event_type");
  const library = request.query.get("library_id");
  const entries =
    type === PASS_EVENT
      ? world.passes
      : type === CLEAN_EVENT
        ? world.cleans
        : [...world.passes, ...world.cleans];
  return activityPage(
    entries.filter(
      (entry) => library === null || entry.library_id === Number(library),
    ),
  );
}

/**
 * Every route the Live view reads, answered from `world` at the time of the request, so a test that changes the world and
 * lets the page read again sees the change. `world` is read, never copied.
 */
export function liveViewRoutes(world: LiveWorld) {
  const folderChains = Object.fromEntries(
    world.workflows.map((workflow) => [
      `GET /api/v1/processing/libraries/${workflow.id}/folder-chain`,
      {
        library_id: workflow.id,
        local: { ready: true, lines: [] },
        managers: [],
        download_clients: [],
        ready: true,
      } satisfies LibraryFolderChain,
    ]),
  );
  const overviews = Object.fromEntries(
    world.workflows.map((workflow) => [
      `GET /api/v1/processing/libraries/${workflow.id}/library-overview`,
      {
        library_id: workflow.id,
        folders_configured: 1,
        breakdowns: {
          audio: [],
          audio_language: [],
          resolution: [],
          subtitle_language: [],
          video_codec: [],
        },
        problems: [],
        scan: null,
        schedule: { enabled: false, next_run_at: null },
        totals: {
          by_status: {
            cant_clean_yet: 0,
            cleaning: 0,
            left_alone: 0,
            matches: 0,
            needs_cleaning: 0,
          },
          cannot_process: 0,
          cleaned: 0,
          estimated_bytes_saved: 0,
          files: 0,
          left_alone: 0,
          matches: 0,
          size_bytes: 0,
          total_removed_audio_tracks: 0,
          total_removed_subtitle_tracks: 0,
          would_change: 0,
        },
      } satisfies LibraryOverview,
    ]),
  );
  const jobs: ProcessingJobsInspectionOut = {
    default_recent_slice: false,
    jobs: [],
  };
  return {
    ...folderChains,
    ...overviews,
    "GET /api/v1/processing/libraries": () => world.workflows,
    "GET /api/v1/processing/files": (request: StubbedRequest) =>
      filesOf(world, request),
    "GET /api/v1/processing/jobs/inspection": jobs,
    "GET /api/v1/processing/files-at-once": () => world.filesAtOnce,
    "GET /api/v1/processing/overview-stats": () => world.stats,
    "GET /api/v1/activity/recent": (request: StubbedRequest) =>
      activityOf(world, request),
    "GET /api/v1/pause": {
      paused: false,
      paused_until: null,
      scan_while_paused: false,
      reason: "",
      in_flight_policy: "finish",
    },
    "GET /api/v1/processing/maintenance": { families: [] },
    "GET /api/v1/suite/settings": {} as AppSettings,
    "GET /api/v1/system/readiness": readyServer() satisfies SystemReadiness,
    "GET /api/v1/media-managers/connections": [],
    "GET /api/v1/download-clients/connections": [],
    "GET /api/v1/system/media-tools": {
      ffmpeg: "ffmpeg version 7.1",
      mkvmerge: "mkvmerge v88.0",
    },
  };
}
