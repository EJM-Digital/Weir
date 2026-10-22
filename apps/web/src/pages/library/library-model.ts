import { formatBytes } from "../../lib/format/bytes";
import type {
  LibraryFile,
  LibraryModeSchedule,
  LibraryProblemKind,
  LibraryTotals,
} from "../../lib/processing/library-mode-api";
import { parseAppDate } from "../../lib/ui/mm-format-date";
import { plural } from "../../lib/ui/mm-plural";
import type { StatusMeaning } from "../../lib/ui/status-meaning";

const MINUTE_MS = 60_000;
/** Past this many minutes a scan's age reads in hours, and past this many hours in days. */
const MINUTES_BEFORE_HOURS = 90;
const HOURS_BEFORE_DAYS = 36;

export const PROBLEM_LABELS: Record<LibraryProblemKind, string> = {
  seeding: "Still seeding",
  manager_redownload: "Your manager would download it again",
  no_permission: "Weir cannot write to it",
  unreadable: "Weir cannot read it",
  no_video: "No video in it",
  no_audio_left: "The rules would leave no audio",
};

/**
 * What a file belongs under: the title its media manager knows it by, or, with no manager, the folders it sits
 * in. A season folder on its own says nothing, so it is shown with the show above it.
 */
export function groupOf(file: LibraryFile): string {
  if (file.manager_title) return file.manager_title;
  const parts = file.path.split(/[\\/]/).filter(Boolean);
  const parent = parts.at(-2);
  const above = parts.at(-3);
  if (parent && above && /^(season|series)\s*\d+$/i.test(parent)) {
    return `${above} · ${parent}`;
  }
  return parent ?? parts[0] ?? "Files";
}

/** Files grouped by title, titles in the order the server first sent one of their files, and the files in its order. */
export function groupFiles(files: LibraryFile[]): [string, LibraryFile[]][] {
  const byTitle = new Map<string, LibraryFile[]>();
  for (const file of files) {
    const key = groupOf(file);
    const list = byTitle.get(key);
    if (list) list.push(file);
    else byTitle.set(key, [file]);
  }
  return [...byTitle.entries()];
}

/**
 * Where a file stands now, against the current rules: exactly one status, as the server works it out, so the counts
 * add up to the files. What Weir once did to a file is history beside its status, never a status of its own.
 */
export type LibraryStatus = LibraryFile["status"];

/** The statuses in the order a person reads them, with the words the chips and the table use. */
export const LIBRARY_STATUSES: readonly LibraryStatus[] = [
  "needs_cleaning",
  "cleaning",
  "matches",
  "cant_clean_yet",
  "left_alone",
];

/**
 * What each status means, so what is shown for it reads the same as the same state anywhere in Weir: a file to clean is
 * waiting for its turn, Weir at work is under way, a match is done, a file held back needs a look, and one a person set
 * aside is idle. A held file Weir cannot reach is the exception, see fileMeaning.
 */
export const STATUS_MEANING: Record<LibraryStatus, StatusMeaning> = {
  needs_cleaning: "todo",
  cleaning: "doing",
  matches: "done",
  cant_clean_yet: "attention",
  left_alone: "idle",
};

/** The problems that mean Weir cannot reach the file at all, which is broken and not merely held back. */
const UNREACHABLE: ReadonlySet<LibraryProblemKind> = new Set([
  "unreadable",
  "no_permission",
]);

/** What a file's status means: its status's meaning, but broken where Weir cannot read or write the file. */
export function fileMeaning(file: LibraryFile): StatusMeaning {
  return file.status === "cant_clean_yet" &&
    file.problem_kind &&
    UNREACHABLE.has(file.problem_kind)
    ? "broken"
    : STATUS_MEANING[file.status];
}

const REASON_WORDS: Record<
  NonNullable<LibraryFile["status_reason"]>,
  string
> = {
  new: "new",
  replaced: "replaced",
  rules_changed: "rules changed",
};

/** The first sentence of why a file is held back, which is as much as the column has room for. */
function firstSentence(text: string): string {
  const end = text.search(/[.!?](\s|$)/);
  return end > 0 ? text.slice(0, end) : text;
}

/** What the table says about a file in as few words as fit the column; the file panel carries the whole sentence. */
export function statusWords(file: LibraryFile): string {
  switch (file.status) {
    case "matches":
      return "Matches your rules";
    case "cleaning":
      return "Cleaning now";
    case "left_alone":
      return "Left alone";
    case "cant_clean_yet": {
      if (file.problem_kind) return PROBLEM_LABELS[file.problem_kind];
      if (file.classification === "cannot_process") {
        return firstSentence(
          (file.reason ?? file.summary ?? "Weir cannot clean this one").trim(),
        );
      }
      return "Still seeding";
    }
    default: {
      const parts: string[] = [];
      if (file.removed_audio_tracks) {
        parts.push(`${file.removed_audio_tracks} audio`);
      }
      if (file.removed_subtitle_tracks) {
        parts.push(
          plural(file.removed_subtitle_tracks, "subtitle", "subtitles"),
        );
      }
      return parts.length
        ? `Would remove ${parts.join(", ")}`
        : "Needs cleaning";
    }
  }
}

/**
 * The quiet note beside a file's status. For a file that needs cleaning it is why, when the scan can say; for one that
 * matches it is what Weir did, "cleaned 3 Oct", or that it was "already clean". It is never a status, and nothing is
 * said where nothing on record supports it.
 */
export function statusNote(file: LibraryFile): string | null {
  if (file.status === "needs_cleaning") {
    return file.status_reason ? REASON_WORDS[file.status_reason] : null;
  }
  if (file.status !== "matches") return null;
  return file.cleaned_at
    ? `cleaned ${new Date(file.cleaned_at * 1000).toLocaleDateString(undefined, { day: "numeric", month: "short" })}`
    : "already clean";
}

/** What the table says when a status is chosen and no file is in it. */
export function emptyStatusLine(status: LibraryStatus): string {
  switch (status) {
    case "needs_cleaning":
      return "No files need cleaning right now.";
    case "cleaning":
      return "No files are being cleaned right now.";
    case "matches":
      return "No files match your rules right now.";
    case "cant_clean_yet":
      return "No files are held back right now.";
    case "left_alone":
      return "You have not set any file aside.";
  }
}

/**
 * What a title with several files comes to: the first of its files to say something, in the order a person reads the
 * statuses: files to clean, then Weir at work, then a file held back, then the files that match, then the rest.
 */
export function groupSummary(files: LibraryFile[]): {
  meaning: StatusMeaning;
  text: string;
} {
  const count = (status: LibraryStatus) =>
    files.filter((file) => file.status === status).length;
  const needing = count("needs_cleaning");
  if (needing > 0) {
    return { meaning: "todo", text: `${needing} need cleaning` };
  }
  const cleaning = count("cleaning");
  if (cleaning > 0) return { meaning: "doing", text: `${cleaning} cleaning` };
  const held = files.filter((file) => file.status === "cant_clean_yet");
  if (held.length > 0) {
    return {
      meaning: held.some((file) => fileMeaning(file) === "broken")
        ? "broken"
        : "attention",
      text: `${held.length} can't clean yet`,
    };
  }
  const matching = count("matches");
  if (matching > 0) return { meaning: "done", text: `${matching} match` };
  return { meaning: "idle", text: `${files.length} left alone` };
}

/**
 * The line under the title: how much is here, and when Weir changes a file. With the daily clean on, Weir
 * changes files without being asked, so the line must not promise otherwise.
 */
export function headerLead(
  totals: LibraryTotals | undefined,
  dailyClean: boolean,
): string {
  const when = dailyClean
    ? "cleans what would change once a day, on this workflow’s schedule."
    : "only changes one when you ask.";
  return totals
    ? `${totals.files.toLocaleString()} files, ${formatBytes(totals.size_bytes)} on your storage. Weir reads them where they are and ${when}`
    : `Weir reads your library where it is and ${when}`;
}

/**
 * When "Scheduled scan and clean" next runs, beside when the library was last checked; nothing while it is off.
 * A run that is due reads as starting now: the server's timer picks it up within half a minute.
 */
export function nextScheduled(
  schedule: LibraryModeSchedule | undefined,
  now: number,
  formatDate: (iso: string) => string,
): string | null {
  if (!schedule?.enabled) return null;
  if (!schedule.next_run_at) {
    return "scheduled check and clean cannot run: no workflow folders, or a schedule window that never opens";
  }
  return parseAppDate(schedule.next_run_at).getTime() <= now
    ? "scheduled check and clean starting now"
    : `next scheduled check and clean ${formatDate(schedule.next_run_at)}`;
}

/** How far ahead the next run is still told as a clock time; further off it needs its day too. */
const CLOCK_ONLY_MS = 18 * 60 * MINUTE_MS;

/**
 * The same as {@link nextScheduled} in the few words the header has room for: "next 10:02 pm", or "next sat 3:00 am"
 * from tomorrow on. The whole sentence stays in the status's tooltip.
 */
export function nextScheduledBrief(
  schedule: LibraryModeSchedule | undefined,
  now: number,
  clock: (ms: number) => string,
  dayClock: (ms: number) => string,
): string | null {
  if (!schedule?.enabled) return null;
  if (!schedule.next_run_at) return "schedule cannot run";
  const at = parseAppDate(schedule.next_run_at).getTime();
  if (at <= now) return "next starting now";
  return `next ${at - now < CLOCK_ONLY_MS ? clock(at) : dayClock(at)}`;
}

/** What the Files card's count says: how many files, and how much room they take. */
export function filesCount(totals: LibraryTotals): string {
  return `${totals.files.toLocaleString()} files · ${formatBytes(totals.size_bytes)}`;
}

/** How long ago the scan that these numbers come from ran. */
export function scanned(generatedAt: number | null, now: number): string {
  if (!generatedAt) return "not scanned yet";
  const minutes = Math.max(
    0,
    Math.round((now - generatedAt * 1000) / MINUTE_MS),
  );
  if (minutes < 1) return "checked just now";
  if (minutes < MINUTES_BEFORE_HOURS) return `checked ${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  return hours < HOURS_BEFORE_DAYS
    ? `checked ${hours} h ago`
    : `checked ${Math.round(hours / 24)} days ago`;
}

/**
 * What cleaning would win back. Never claims there is nothing to reclaim when Weir simply could not
 * measure it: some files carry no per-track size for the scan to add up.
 */
export function savingLine(totals: LibraryTotals | undefined): string {
  if (totals && totals.estimated_bytes_saved > 0) {
    return `About ${formatBytes(totals.estimated_bytes_saved)} back if everything that would change is cleaned`;
  }
  if (totals && totals.would_change > 0) {
    const tracks =
      totals.total_removed_audio_tracks + totals.total_removed_subtitle_tracks;
    return `${plural(tracks, "track", "tracks")} would come out; these files do not say how big each one is`;
  }
  return "Nothing to reclaim here at the moment";
}
