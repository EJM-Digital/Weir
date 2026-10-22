/**
 * Which lane each file belongs in on the Processing page, and the words each card shows. Pure
 * functions, so the rules are tested on their own. Every value comes from the server: file states and
 * the running pass's own progress from `GET /api/v1/processing/files`, library cleans from the job queue.
 */
import { formatBytes } from "../../lib/format/bytes";
import type { ProcessingFile } from "../../lib/processing/files-api";
import type { ProcessingJobInspectionRow } from "../../lib/processing/jobs-inspection/types";
import { baseName } from "../../lib/format/path";
import { parseAppTime } from "../../lib/ui/mm-format-date";
import { stepForStage, type FlowStepId } from "./stage-flow-model";

export const LIBRARY_CLEAN_JOB_KIND = "processing.library.clean.v1";

/** The job kinds that are a library's own work: its scan and its clean. */
const LIBRARY_JOB_KIND_PREFIX = "processing.library.";
/** The job kinds that are new-download work: a file's pass, and the scan of a watched folder that finds it. */
const DOWNLOAD_JOB_KIND_PREFIXES = [
  "processing.file.",
  "processing.watched_folder.",
] as const;

/** The file status the Working lane shows (see the `case "processing"` below). */
export const WORKING_FILE_STATUS: ProcessingFile["status"] = "processing";

export type WorkSource = "download" | "library";

/** Which kind of work a job of this kind is. A job that is neither (a cleanup sweep, say) is Weir's own housekeeping: null. */
export function workSourceOfJobKind(jobKind: string): WorkSource | null {
  if (jobKind.startsWith(LIBRARY_JOB_KIND_PREFIX)) return "library";
  return DOWNLOAD_JOB_KIND_PREFIXES.some((prefix) => jobKind.startsWith(prefix))
    ? "download"
    : null;
}

export type ArrivingItem = {
  key: string;
  file: ProcessingFile;
  name: string;
  facts: string;
  /** The file's path, so its full name can be shown under the friendly title. */
  path: string;
  note: string;
  /** When the wait ends (epoch ms), for a countdown. Null when the wait is on a writer, not a clock. */
  holdUntil: number | null;
  /** How long the whole wait is (seconds), so the ring can show how much of it has passed. */
  holdTotal: number | null;
  /** The media manager still has it (blocked upstream). */
  upstream: boolean;
  /**
   * For a wait with no clock of its own (a manager still importing it, a file Weir cannot read yet): when Weir next
   * looks at the library (epoch ms) and how often it looks (seconds), so the card counts down to that instead.
   */
  nextLook: { at: number; interval: number } | null;
};

/** When an arriving file's wait next changes: its own hold, or failing that Weir's next look at its library. */
export function arrivingDeadline(item: ArrivingItem): number | null {
  return item.holdUntil ?? item.nextLook?.at ?? null;
}

export type WaitingItem = {
  key: string;
  source: WorkSource;
  name: string;
  /** The file's path, so its full name can be shown under the friendly title. */
  path: string;
  facts: string;
  note: string | null;
  libraryName: string;
  file: ProcessingFile | null;
};

export type WorkingItem = {
  key: string;
  source: WorkSource;
  name: string;
  /** The file's path, so its full name can be shown under the friendly title. */
  path: string;
  facts: string;
  libraryName: string;
  /** The step of its stages the pass is on. */
  step: FlowStepId;
  percent: number | null;
  etaSeconds: number | null;
  speed: string | null;
  removedAudio: number;
  removedSubtitles: number;
  file: ProcessingFile | null;
};

export type HandingItem = {
  key: string;
  source: WorkSource;
  name: string;
  /** The file's path, so its full name can be shown under the friendly title. */
  path: string;
  libraryName: string;
  /** The step of its stages the pass is on. */
  step: FlowStepId;
  file: ProcessingFile | null;
};

export type Lanes = {
  arriving: ArrivingItem[];
  waiting: WaitingItem[];
  working: WorkingItem[];
  handing: HandingItem[];
};

const EPISODE = /^(.+?)[ ._-]+(S\d{1,2}E\d{1,3}(?:-?E\d{1,3})?)(?:[ ._-]|$)/i;
const FILM = /^(.+?)[ ._-]+\(?((?:19|20)\d{2})\)?(?:[ ._-]|$)/;

function words(raw: string): string {
  return raw.replace(/[._]+/g, " ").replace(/\s+/g, " ").trim();
}

/** "The.Quiet.Harbour.S01E03.1080p.WEB-DL.mkv" reads as "The Quiet Harbour S01E03". */
export function prettyName(path: string): string {
  const base = baseName(path);
  const stem = base.replace(/\.[a-z0-9]{2,4}$/i, "");
  const episode = EPISODE.exec(stem);
  if (episode) return `${words(episode[1])} ${episode[2].toUpperCase()}`;
  const film = FILM.exec(stem);
  if (film) return `${words(film[1])} (${film[2]})`;
  return words(stem) || base;
}

/** "1080p · H264 · 2.27 GB": what the last probe measured. Missing facts are left out, never guessed. */
export function fileFacts(file: ProcessingFile): string {
  const bits: string[] = [];
  if (file.video_height) bits.push(`${file.video_height}p`);
  if (file.video_codec) bits.push(file.video_codec.toUpperCase());
  bits.push(formatBytes(file.size_bytes));
  return bits.join(" · ");
}

/** The first sentence of a reason Weir wrote, which is the part that fits on a card. */
export function firstSentence(text: string | null | undefined): string {
  const t = (text ?? "").trim();
  const end = t.search(/[.!?](\s|$)/);
  return end > 0 ? t.slice(0, end + 1) : t;
}

/** Seconds until a wait ends, rounded up, never negative. Null when there is no clock on it. */
export function secondsLeft(
  holdUntil: number | null,
  now: number,
): number | null {
  if (holdUntil == null) return null;
  return Math.max(0, Math.ceil((holdUntil - now) / 1000));
}

function libraryJobParts(row: ProcessingJobInspectionRow): {
  path: string;
  libraryId: number | null;
} {
  try {
    const payload = JSON.parse(row.payload_json ?? "{}") as {
      path?: unknown;
      library_id?: unknown;
    };
    return {
      path: typeof payload.path === "string" ? payload.path : "",
      libraryId:
        typeof payload.library_id === "number" ? payload.library_id : null,
    };
  } catch {
    return { path: "", libraryId: null };
  }
}

/** The workflow a library clean job belongs to, or null when its payload does not say. */
export function libraryCleanWorkflowId(
  row: ProcessingJobInspectionRow,
): number | null {
  return libraryJobParts(row).libraryId;
}

/**
 * The general, paginated file list with the Working lane's own uncapped, status-filtered fetch folded in
 * (#781): a currently-processing file the general page's own limit left out is patched back onto the end,
 * so a running title's row can never be lost to it. A file the general page already has keeps its place
 * there; `workingFiles` only ever adds the ones missing, never duplicates or reorders what is already shown.
 */
export function mergeWorkingFiles(
  files: ProcessingFile[],
  workingFiles: ProcessingFile[],
): ProcessingFile[] {
  if (workingFiles.length === 0) return files;
  const onPage = new Set(files.map((file) => file.id));
  const missing = workingFiles.filter((file) => !onPage.has(file.id));
  return missing.length === 0 ? files : [...files, ...missing];
}

/**
 * Sorts files and library clean jobs into the lanes. Arriving: held for a reason that ends by itself,
 * or still with the media manager. Waiting: ready, or outside its hours. Working: a pass is writing it.
 * Handing back: on its final checks. Stuck: failed, or held after repeated failures. Library cleans
 * come from the job queue and report no percentage, so their card says what is happening without one.
 */
export function buildLanes(
  files: ProcessingFile[],
  libraryJobs: ProcessingJobInspectionRow[],
  libraryNames: Map<number, string>,
  readyAfterByLibrary: Map<number, number>,
  nextLookByLibrary: Map<number, { at: number; interval: number }> = new Map(),
): Lanes {
  const lanes: Lanes = {
    arriving: [],
    waiting: [],
    working: [],
    handing: [],
  };

  for (const file of files) {
    const name = prettyName(file.relative_path);
    const facts = fileFacts(file);
    const libraryName =
      file.library_name || libraryNames.get(file.library_id) || "Workflow";
    const key = `file-${file.id}`;
    switch (file.status) {
      case "on_hold": {
        // A file Weir has found gone waits for nothing: no clock, and no look at its workflow to count down to.
        const gone = file.source_gone === true;
        const holdUntil = gone ? null : parseAppTime(file.hold_until);
        const since =
          parseAppTime(file.size_changed_at) ?? parseAppTime(file.updated_at);
        const readyAfter = readyAfterByLibrary.get(file.library_id) ?? null;
        const holdTotal =
          holdUntil != null && since != null && holdUntil > since
            ? (holdUntil - since) / 1000
            : readyAfter;
        lanes.arriving.push({
          key,
          file,
          name,
          path: file.relative_path,
          facts,
          note: firstSentence(file.status_reason),
          holdUntil,
          holdTotal,
          upstream: false,
          nextLook:
            holdUntil == null && !gone
              ? (nextLookByLibrary.get(file.library_id) ?? null)
              : null,
        });
        break;
      }
      case "blocked_upstream":
        lanes.arriving.push({
          key,
          file,
          name,
          path: file.relative_path,
          facts,
          note: file.blocked_by_connection
            ? `${file.blocked_by_connection} is still importing it.`
            : firstSentence(file.status_reason),
          holdUntil: null,
          holdTotal: null,
          upstream: true,
          nextLook: nextLookByLibrary.get(file.library_id) ?? null,
        });
        break;
      case "unprocessed":
      case "out_of_schedule":
        lanes.waiting.push({
          key,
          source: "download",
          name,
          path: file.relative_path,
          facts,
          note:
            file.status === "out_of_schedule"
              ? firstSentence(file.status_reason)
              : null,
          libraryName,
          file,
        });
        break;
      case "processing": {
        if (file.progress_status === "finishing") {
          lanes.handing.push({
            key,
            source: "download",
            name,
            path: file.relative_path,
            libraryName,
            step: stepForStage(file.progress_stage, "verify"),
            file,
          });
          break;
        }
        lanes.working.push({
          key,
          source: "download",
          name,
          path: file.relative_path,
          facts,
          libraryName,
          step: stepForStage(
            file.progress_stage,
            file.progress_percent == null ? "checking" : "write",
          ),
          percent: file.progress_percent,
          etaSeconds: file.progress_eta_seconds,
          speed: file.progress_speed ?? null,
          removedAudio: file.progress_removed_audio?.length ?? 0,
          removedSubtitles: file.progress_removed_subtitles?.length ?? 0,
          file,
        });
        break;
      }
      default:
        break;
    }
  }

  for (const row of libraryJobs) {
    if (row.job_kind !== LIBRARY_CLEAN_JOB_KIND) continue;
    const { path, libraryId } = libraryJobParts(row);
    const libraryName =
      (libraryId != null && libraryNames.get(libraryId)) || "Workflow";
    const item = {
      key: `job-${row.id}`,
      source: "library" as const,
      name: prettyName(path || `Library file ${row.id}`),
      path,
      facts: "Cleaning in place",
      libraryName,
      file: null,
    };
    if (row.status === "leased") {
      lanes.working.push({
        ...item,
        step: "write",
        percent: null,
        etaSeconds: null,
        speed: null,
        removedAudio: 0,
        removedSubtitles: 0,
      });
    } else if (row.status === "pending") {
      lanes.waiting.push({ ...item, note: null });
    }
  }

  // The file closest to its next moment (its own hold, or Weir's next look) first; the rest keep the server's order.
  lanes.arriving.sort(
    (a, b) =>
      (arrivingDeadline(a) ?? Infinity) - (arrivingDeadline(b) ?? Infinity),
  );
  // A running pass keeps its place. The server lists files by when they were last seen, which moves while they run,
  // and two cards that trade rows glide through one another.
  lanes.working.sort(byKey);
  lanes.handing.sort(byKey);
  return lanes;
}

/** Orders items by their key, the numbers in it as numbers: "file-9" before "file-10". */
function byKey(a: { key: string }, b: { key: string }): number {
  return a.key.localeCompare(b.key, undefined, { numeric: true });
}

/** How many cards the Working lane holds for these files and jobs, before the page's own filter. */
export function countWorking(
  files: ProcessingFile[],
  libraryJobs: ProcessingJobInspectionRow[],
): number {
  return buildLanes(files, libraryJobs, new Map(), new Map()).working.length;
}
