import {
  FINISHED_MEANING,
  type FinishedFile,
} from "../../../lib/activity/processing-outcome";
import type { StatusMeaning } from "../../../lib/ui/status-meaning";
import { formatBytes } from "../../../lib/format/bytes";
import { parseAppTime } from "../../../lib/ui/mm-format-date";
import { shownBy, type Filter } from "../processing-filter";
import { prettyName } from "../processing-model";
import { ago, finishedLine, removedTrackWords } from "../processing-words";

/** The workflow's name for a file whose entry names no workflow. */
const UNKNOWN_WORKFLOW = "Workflow";

/** The most tiles the shelf holds: the room it has decides how many of them show. */
export const SHELF_LIMIT = 12;

/** The words one caption line can say, fullest first: the first that fits the line is shown, the last always is (cut by an ellipsis if need be). */
export type LineWords = readonly string[];

/**
 * The caption's status line: what it means, and the words it can say. `short` is what the line says under a poster with no
 * room for a title, and what it falls back on where `words` do not fit: the briefest words, the last of them always a
 * word and never nothing.
 */
export type TileStatus = {
  meaning: StatusMeaning;
  /** The words are the figure of space saved, which is drawn in gold: the dot beside it still says the file is done. */
  payoff?: true;
  words: LineWords;
  short: LineWords;
};

/** One finished file as a tile on the Just finished shelf. */
export type ShelfTile = {
  key: string;
  path: string;
  title: string;
  /** The workflow's name, which the tile is tinted for. */
  workflow: string;
  /** Whether the workflow is known: an unknown one is tinted but not named on the tile. */
  workflowKnown: boolean;
  /** The caption's status line: how the file came out, "12 GB saved", "Already clean". */
  status: TileStatus;
  /** The full caption's detail line: what was removed, "−1 audio · −7 subs", then "−8 tracks"; null where there is nothing to say. */
  detail: LineWords | null;
  /** What was done to it, in a few words, for the tile's name. */
  what: string;
  /** What the file shrank by, "318 MB", for the tile's name; nothing when no sizes were recorded. */
  savedAmount: string | null;
  /** The full caption's last line: "just now", "3 min ago". */
  ago: string;
  /** The whole sentence, for the tooltip. */
  sentence: string;
  item: FinishedFile;
};

/** What Just finished adds up to: today's counts and the tiles to show, newest first. */
export type Shelf = {
  /** Files that finished today, and what they add up to. */
  today: number;
  savedBytes: number;
  /** Nothing finished today, so the tiles are the latest from before. */
  latest: boolean;
  tiles: ShelfTile[];
};

export type ShelfScope = {
  filter: Filter;
  /** Only this workflow's files; every workflow's when null. */
  workflowId: number | null;
  now: number;
};

const SHORT_WHAT: Partial<Record<FinishedFile["kind"], string>> = {
  already: "Already clean",
  passed: "Passed through unchanged",
  rejected: "Rejected",
  failed: "Could not be finished",
};

/** What was done to a file, in a few words. */
function whatWasDone(item: FinishedFile): string {
  const removed = removedTrackWords(item);
  if (removed.length > 0) return `${removed.join(", ")} removed`;
  return (
    SHORT_WHAT[item.kind] ??
    (item.source === "library" ? "Cleaned in place" : "Cleaned")
  );
}

const GIB = 1024 ** 3;
const MIB = 1024 ** 2;

const KIB = 1024;

/** A saving in the few characters a caption has: one decimal for GB, whole MB or KB, "1.2 GB", "221 MB", "318 KB". */
export function savedSize(bytes: number): string {
  if (bytes >= GIB) return `${(bytes / GIB).toFixed(1)} GB`;
  if (bytes < MIB) {
    const kb = Math.max(1, Math.round(bytes / KIB));
    if (kb < KIB) return `${kb} KB`;
  }
  const mb = Math.round(bytes / MIB);
  return mb >= 1024 ? "1.0 GB" : `${Math.max(1, mb)} MB`;
}

/** How a file stands, for the caption's status line: a cleaned file's saving, and a word for any other outcome. */
function statusOf(item: FinishedFile): TileStatus {
  const meaning = FINISHED_MEANING[item.kind];
  switch (item.kind) {
    case "already":
      return { meaning, words: ["Already clean"], short: ["Clean"] };
    case "passed":
      return { meaning, words: ["Passed through"], short: ["Passed"] };
    case "rejected":
      return { meaning, words: ["Rejected"], short: ["Rejected"] };
    case "failed":
      return { meaning, words: ["Couldn't finish"], short: ["Failed"] };
    default: {
      if (!item.savedBytes) {
        return { meaning, words: ["Cleaned"], short: ["Cleaned", "Done"] };
      }
      const size = savedSize(item.savedBytes);
      return {
        meaning,
        payoff: true,
        words: [`${size} saved`, size],
        short: [size],
      };
    }
  }
}

/** What was taken out of a cleaned file, the minus meaning removed: "−1 audio · −7 subs", then "−8 tracks". */
function detailOf(item: FinishedFile): LineWords | null {
  if (item.kind !== "cleaned") return null;
  const { removedAudio: audio, removedSubtitles: subs } = item;
  if (audio === 0 && subs === 0) {
    return item.source === "library" ? ["Cleaned in place"] : null;
  }
  const parts = [
    audio > 0 ? `−${audio} audio` : null,
    subs > 0 ? `−${subs} ${subs === 1 ? "sub" : "subs"}` : null,
  ].filter(Boolean);
  const tracks = audio + subs;
  return [parts.join(" · "), `−${tracks} ${tracks === 1 ? "track" : "tracks"}`];
}

function isToday(iso: string, now: number): boolean {
  const at = parseAppTime(iso);
  return (
    at != null && new Date(at).toDateString() === new Date(now).toDateString()
  );
}

function tileOf(
  item: FinishedFile,
  now: number,
  names: ReadonlyMap<number, string>,
): ShelfTile {
  const workflow =
    item.libraryId == null ? undefined : names.get(item.libraryId);
  const savedAmount = item.savedBytes ? formatBytes(item.savedBytes) : null;
  return {
    key: String(item.id),
    path: item.relativePath,
    title: prettyName(item.relativePath),
    workflow: workflow || UNKNOWN_WORKFLOW,
    workflowKnown: Boolean(workflow),
    status: statusOf(item),
    detail: detailOf(item),
    what: whatWasDone(item),
    savedAmount,
    ago: ago(item.finishedAt, now),
    sentence: finishedLine(item),
    item,
  };
}

/**
 * The shelf for the files the page's filter and workflow let through: today's files if there are any,
 * and otherwise the latest from before.
 */
export function shelfOf(
  items: readonly FinishedFile[],
  scope: ShelfScope,
  names: ReadonlyMap<number, string>,
): Shelf {
  const wanted = items.filter(
    (item) =>
      shownBy(scope.filter, item) &&
      (scope.workflowId === null || item.libraryId === scope.workflowId),
  );
  const today = wanted.filter((item) => isToday(item.finishedAt, scope.now));
  const latest = today.length === 0;
  return {
    today: today.length,
    savedBytes: today.reduce((sum, item) => sum + (item.savedBytes ?? 0), 0),
    latest,
    tiles: (latest ? wanted : today)
      .slice(0, SHELF_LIMIT)
      .map((item) => tileOf(item, scope.now, names)),
  };
}

/** "12 today · 41.20 GB saved": what the shelf's files finished today. */
export function todayWords(shelf: Shelf): string {
  if (shelf.latest) return "Nothing yet today · latest arrivals";
  const saved =
    shelf.savedBytes > 0 ? ` · ${formatBytes(shelf.savedBytes)} saved` : "";
  return `${shelf.today} today${saved}`;
}
