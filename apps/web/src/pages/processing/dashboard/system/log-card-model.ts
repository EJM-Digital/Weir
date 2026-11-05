/** What the Log card says: Weir's log lines as levels, merged from the first read and the live frames, and counted for today. */
import type { SystemLogLevel } from "../../../../lib/system/system-log-api";
import type { SystemLogFrame } from "../../../../lib/system/system-log-frame";
import { parseAppTime } from "../../../../lib/ui/mm-format-date";
import { plural } from "../../../../lib/ui/mm-plural";
import { dayKey } from "./system-time";

/** The levels the card shows: a success is not a line the card keeps. */
export type LogLevel = Exclude<SystemLogLevel, "success">;
/** What the switch shows: errors and warnings together, everything, or one level. */
export type LogFilter = "problems" | "all" | "error" | "warning";

export type LogLine = {
  /** The same line read twice has the same key, so a line is listed once. */
  key: string;
  /** When it was written, in ms since the epoch. */
  at: number;
  level: LogLevel;
  message: string;
  /** When this page was told of it, for a line that arrived on the stream: it pulses for a moment. Null for a line read from the log. */
  arrivedAt: number | null;
};

/** How long a line that has just arrived pulses. */
export const PULSE_MS = 1500;
/** The most lines the card keeps. */
export const MOST_LOG_LINES = 250;

export const LOG_FILTERS: readonly { value: LogFilter; label: string }[] = [
  { value: "problems", label: "Problems" },
  { value: "all", label: "All" },
  { value: "error", label: "Errors" },
  { value: "warning", label: "Warnings" },
];

/** A level as the log writes it (INFO, WARNING, ERROR, in any case; CRITICAL counts as an error). */
export function levelOf(raw: string): LogLevel {
  switch (raw.trim().toUpperCase()) {
    case "ERROR":
    case "CRITICAL":
      return "error";
    case "WARNING":
    case "WARN":
      return "warning";
    default:
      return "info";
  }
}

function lineOf(
  at: string,
  rawLevel: string,
  message: string,
  arrivedAt: number | null,
): LogLine | null {
  const time = parseAppTime(at);
  if (time === null) return null;
  const level = levelOf(rawLevel);
  return {
    // The stream and the log read both write the server's one timestamp (to the microsecond) and level, and the stream
    // trims the message, so a line keeps its key, and its element, when the read returns the line the stream brought.
    key: `${time}|${level}|${message.trim()}`,
    at: time,
    level,
    message,
    arrivedAt,
  };
}

/** One entry of `GET /suite/logs`, or null when its time cannot be read. */
export function lineFromEntry(entry: {
  timestamp: string;
  level: string;
  message: string;
}): LogLine | null {
  return lineOf(entry.timestamp, entry.level, entry.message, null);
}

/** One `system.log` frame, noted as arriving at `arrivedAt`. */
export function lineFromFrame(
  frame: SystemLogFrame,
  arrivedAt: number,
): LogLine | null {
  return lineOf(frame.at, frame.level, frame.message, arrivedAt);
}

/**
 * Every line of every group once, newest first. A line that is in more than one group (read from the log after it
 * arrived on the stream) keeps the version from the earlier group, so a live line is still marked as live.
 */
export function mergeLines(
  groups: readonly (readonly LogLine[])[],
  limit = MOST_LOG_LINES,
): LogLine[] {
  const byKey = new Map<string, LogLine>();
  for (const line of groups.flat()) {
    if (!byKey.has(line.key)) byKey.set(line.key, line);
  }
  return [...byKey.values()].sort((a, b) => b.at - a.at).slice(0, limit);
}

/** The lines the switch shows. */
export function shownLines(
  lines: readonly LogLine[],
  filter: LogFilter,
): LogLine[] {
  switch (filter) {
    case "all":
      return [...lines];
    case "problems":
      return lines.filter((line) => line.level !== "info");
    default:
      return lines.filter((line) => line.level === filter);
  }
}

/**
 * What the switch shows until a person picks: the problems when there have been any today, since they are what the
 * card is for and the information that fills the log would bury them, else everything.
 */
export function defaultFilter(counts: LogCounts): LogFilter {
  return counts.errors + counts.warnings > 0 ? "problems" : "all";
}

/** Whether a line has just arrived and should still pulse. */
export function isFresh(line: LogLine, now: number): boolean {
  return line.arrivedAt !== null && now - line.arrivedAt < PULSE_MS;
}

export type LogCounts = { errors: number; warnings: number };

/** How many errors and warnings were written today, where today is the day `now` falls on in the timezone. */
export function todayCounts(
  lines: readonly LogLine[],
  now: number,
  timeZone: string | undefined,
): LogCounts {
  const today = dayKey(now, timeZone);
  const written = lines.filter((line) => dayKey(line.at, timeZone) === today);
  return {
    errors: written.filter((line) => line.level === "error").length,
    warnings: written.filter((line) => line.level === "warning").length,
  };
}

/** The few words beside the card's title: "0 errors · 3 warnings today". */
export function logSummary(counts: LogCounts): string {
  return `${plural(counts.errors, "error", "errors")} · ${plural(counts.warnings, "warning", "warnings")} today`;
}

/** The summary in words that narrow with the room: the day goes first, then the warnings, then the errors, and last nothing (the title holds the whole) rather than a cut word. */
export function logSummaryWords(counts: LogCounts): string[] {
  const errors = plural(counts.errors, "error", "errors");
  return [
    logSummary(counts),
    `${errors} · ${plural(counts.warnings, "warning", "warnings")}`,
    errors,
    "",
  ];
}

const NOTHING_AT_ALL = "Nothing has been logged yet.";
const NOTHING_AT_LEVEL: Record<Exclude<LogFilter, "all">, string> = {
  problems: "No errors or warnings in the log.",
  error: "No errors in the log.",
  warning: "No warnings in the log.",
};

/** What the card says when the switch shows no line. */
export function emptyLogWords(filter: LogFilter): string {
  return filter === "all" ? NOTHING_AT_ALL : NOTHING_AT_LEVEL[filter];
}
