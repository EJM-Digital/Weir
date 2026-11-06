import { holdResolvesItself } from "../processing/reason-words";
import {
  isBelowMinimumSize,
  type ProcessingFile,
  type ProcessingFileStatus,
} from "../../lib/processing/files-api";
import type { LibraryClean } from "../../lib/processing/library-cleans-api";
import type { WorkflowKind } from "../../lib/processing/workflow-kind";
import type { StatusMeaning } from "../../lib/ui/status-meaning";
import {
  meaningRank,
  sortRows,
  type SortValue,
  type TableSort,
} from "../../lib/ui/table-columns";
import type { ActivityColumnId } from "./activity-columns";
import { awaitsImport, serverMs, type WorkflowKindOf } from "./activity-model";

/**
 * One row in Activity: a new download Weir processed, or a file it cleaned where it sits in a library (#695). Both are
 * listed together, so Activity is the one place to look for what happened to a file. A download whose cleaned copy is
 * still waiting for a media manager to take it says so: it reads as waiting, though Weir's own work on it is done.
 */
export type ActivityEntry =
  | {
      kind: "download";
      key: string;
      file: ProcessingFile;
      awaitingManager: boolean;
    }
  | { kind: "library_clean"; key: string; clean: LibraryClean };

export const downloadEntry = (
  file: ProcessingFile,
  workflowKind: WorkflowKind["kind"] | null = null,
): ActivityEntry => ({
  kind: "download",
  key: `download-${file.id}`,
  file,
  awaitingManager:
    workflowKind !== null && awaitsImport(file.handback, workflowKind),
});

export const cleanEntry = (clean: LibraryClean): ActivityEntry => ({
  kind: "library_clean",
  key: `clean-${clean.id}`,
  clean,
});

/**
 * The ways Activity sorts an entry, in the order the chips read. "kept" is not an entry group at all — a kept
 * file has no `files` row left to be one (#786 review of #785) — but it lives in the same chip row and count
 * pattern, since removing a title to keep it is still something Activity did with it. "attention" is not one group
 * either: it is every file that waits on a person, whichever group it is in, as the sidebar's badge counts them.
 */
export type ActivityGroup =
  | "all"
  | "working"
  | "finished"
  | "attention"
  | "needs"
  | "skipped"
  | "failed"
  | "kept";

export const ACTIVITY_GROUPS: { id: ActivityGroup; label: string }[] = [
  { id: "all", label: "All" },
  { id: "working", label: "In progress" },
  { id: "finished", label: "Finished" },
  { id: "attention", label: "Needs you" },
  { id: "needs", label: "On hold" },
  { id: "skipped", label: "Skipped" },
  { id: "failed", label: "Failed" },
  { id: "kept", label: "Kept" },
];

const WORKING: readonly ProcessingFileStatus[] = [
  "unprocessed",
  "processing",
  "out_of_schedule",
  "blocked_upstream",
];
const FINISHED: readonly ProcessingFileStatus[] = [
  "processed",
  "passed_through",
];
const FAILED: readonly ProcessingFileStatus[] = [
  "processing_failed",
  "rejected",
];

const CLEAN_GROUPS: Record<LibraryClean["outcome"], ActivityGroup> = {
  cleaned: "finished",
  skipped: "skipped",
  failed: "failed",
};

/**
 * What each state of a download means, whatever group it is listed under: a file waiting for its turn, or for its media
 * manager to take it, is still to do, one Weir is writing is under way, one it handed back is done, and one that passed
 * through, was rejected or is held needs a look. Only a failure is broken.
 */
export const FILE_MEANING: Record<ProcessingFileStatus, StatusMeaning> = {
  unprocessed: "todo",
  out_of_schedule: "todo",
  processing: "doing",
  processed: "done",
  processing_failed: "broken",
  on_hold: "attention",
  blocked_upstream: "todo",
  passed_through: "attention",
  rejected: "attention",
  skipped: "idle",
  disabled: "idle",
  cancelled: "idle",
};

/** What a clean in a library means: a file already matching the rules is as done as one just cleaned. */
export const CLEAN_MEANING: Record<LibraryClean["outcome"], StatusMeaning> = {
  cleaned: "done",
  skipped: "done",
  failed: "broken",
};

/** The state of a download whose media manager has not yet taken it: the original, or the copy Weir handed back. */
const WAITING_FOR_MANAGER: ProcessingFileStatus = "blocked_upstream";

export function entryMeaning(entry: ActivityEntry): StatusMeaning {
  if (entry.kind === "library_clean") return CLEAN_MEANING[entry.clean.outcome];
  return FILE_MEANING[
    entry.awaitingManager ? WAITING_FOR_MANAGER : entry.file.status
  ];
}

/**
 * Where a download belongs. A file on hold waits on a person; one its media manager still has is only waiting its turn,
 * so it is "In progress"; a file Weir has given up on is "Failed", with its reason; a skip is Weir deciding a file is
 * not for it, which is not a failure. A library that is off or a cancelled pass is neither working nor finished, so it only shows
 * under All.
 */
export function activityGroupOf(file: ProcessingFile): ActivityGroup | null {
  if (file.status === "on_hold") return "needs";
  if (WORKING.includes(file.status)) return "working";
  if (FINISHED.includes(file.status)) return "finished";
  if (file.status === "skipped") return "skipped";
  if (FAILED.includes(file.status)) return "failed";
  return null;
}

/**
 * How the server words a skip for one of the workflow's own rules: its path, size or dates. Any other skip is
 * Weir deciding a file is not for it, which needs nobody.
 */
const SKIPPED_BY_RULE = /^skipped because/i;

/**
 * Whether a file waits on a person: a failure, a rejection, a hold with no clock on it that is not Weir's own wait to settle, or a skip by a rule. A file under the
 * workflow's minimum size is left out: the minimum is doing its job, skipping samples and extras. The badge, Needs you panel
 * and Activity chip count the same files.
 */
export function waitsOnAPerson(file: ProcessingFile): boolean {
  switch (file.status) {
    case "processing_failed":
    case "rejected":
      return true;
    case "on_hold":
      return !file.hold_until && !holdResolvesItself(file.status_reason);
    case "skipped":
      return (
        SKIPPED_BY_RULE.test(file.status_reason) && !isBelowMinimumSize(file)
      );
    default:
      return false;
  }
}

/** A download whose cleaned copy waits for a media manager is still in progress, though Weir has finished with it. */
export function entryGroup(entry: ActivityEntry): ActivityGroup | null {
  if (entry.kind === "library_clean") return CLEAN_GROUPS[entry.clean.outcome];
  return entry.awaitingManager ? "working" : activityGroupOf(entry.file);
}

export function inGroup(entry: ActivityEntry, group: ActivityGroup): boolean {
  if (group === "all") return true;
  if (group === "attention") {
    return entry.kind === "download" && waitsOnAPerson(entry.file);
  }
  return entryGroup(entry) === group;
}

/** When the entry last changed: a download's last state change, or when the clean finished. */
export function entryTime(entry: ActivityEntry): string {
  return entry.kind === "download"
    ? entry.file.updated_at
    : entry.clean.recorded_at;
}

export function entryPath(entry: ActivityEntry): string {
  return entry.kind === "download"
    ? entry.file.relative_path
    : entry.clean.relative_path;
}

/** Both kinds together, newest change first, so what just happened is at the top. */
export function activityEntries(
  files: ProcessingFile[],
  cleans: LibraryClean[],
  workflowKindOf: WorkflowKindOf = () => null,
): ActivityEntry[] {
  return [
    ...files.map((file) => downloadEntry(file, workflowKindOf(file))),
    ...cleans.map(cleanEntry),
  ].sort((a, b) => serverMs(entryTime(b)) - serverMs(entryTime(a)));
}

/** What each column of Activity's list sorts by: the file's path, what happened by its meaning, and when it last changed. */
const ENTRY_SORT_VALUES: Record<
  ActivityColumnId,
  (entry: ActivityEntry) => SortValue
> = {
  file: entryPath,
  status: (entry) => meaningRank(entryMeaning(entry)),
  when: (entry) => serverMs(entryTime(entry)),
};

/** The entries in the order a heading sorts them. The server sorts the downloads the same way before it pages them. */
export function sortActivityEntries(
  entries: readonly ActivityEntry[],
  sort: TableSort<ActivityColumnId> | null,
): ActivityEntry[] {
  return sortRows(entries, sort, ENTRY_SORT_VALUES);
}

/** Whether any listed download was rejected: what "Process all again" is offered for. */
export function hasRejectedFiles(entries: ActivityEntry[]): boolean {
  return entries.some(
    (entry) => entry.kind === "download" && entry.file.status === "rejected",
  );
}

/** The failed downloads that "Try again" can queue: a failed library clean is cleaned again from Library. */
export function retryableFailures(entries: ActivityEntry[]): ProcessingFile[] {
  return entries.flatMap((entry) =>
    entry.kind === "download" && entry.file.status === "processing_failed"
      ? [entry.file]
      : [],
  );
}
