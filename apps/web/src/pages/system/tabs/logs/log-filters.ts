import type {
  SystemLogCategory,
  SystemLogLevel,
  SystemLogQuery,
  SystemLogSource,
} from "../../../../lib/system/system-log-api";
import {
  instantFromLocalInput,
  startOfDayAt,
} from "../../../../lib/ui/zoned-time";

/** The parts of the time: how far back the log is read. */
export type LogWhen = "any" | "hour" | "today" | "day" | "week" | "custom";

export const LOG_WHEN_OPTIONS: readonly { value: LogWhen; label: string }[] = [
  { value: "any", label: "Any time" },
  { value: "hour", label: "Last hour" },
  { value: "today", label: "Today" },
  { value: "day", label: "Last 24 hours" },
  { value: "week", label: "Last 7 days" },
  { value: "custom", label: "Custom range" },
];

export const LOG_SOURCES: readonly {
  value: SystemLogSource;
  label: string;
}[] = [
  { value: "event", label: "Events" },
  { value: "job", label: "Jobs" },
  { value: "server", label: "Server" },
];

/**
 * What the level picker offers. A choice stands for the levels it names: "Info" is information and successes, so
 * everything that is not a problem is one choice, and the dot of a row says which of the two it is.
 */
export const LOG_LEVEL_CHOICES: readonly {
  value: string;
  label: string;
  levels: readonly SystemLogLevel[];
}[] = [
  { value: "error", label: "Errors", levels: ["error"] },
  { value: "warning", label: "Warnings", levels: ["warning"] },
  { value: "info", label: "Info", levels: ["info", "success"] },
];

/** The picker's choices that any of `levels` falls under. */
export function levelChoicesFor(levels: readonly SystemLogLevel[]): string[] {
  return LOG_LEVEL_CHOICES.filter((choice) =>
    choice.levels.some((level) => levels.includes(level)),
  ).map((choice) => choice.value);
}

/** The levels the chosen choices stand for. Choosing every choice is the same as choosing none: every level. */
export function levelsForChoices(chosen: readonly string[]): SystemLogLevel[] {
  if (chosen.length === LOG_LEVEL_CHOICES.length) return [];
  return LOG_LEVEL_CHOICES.filter((choice) =>
    chosen.includes(choice.value),
  ).flatMap((choice) => choice.levels);
}

export const LOG_CATEGORY_LABELS: Record<SystemLogCategory, string> = {
  processing: "Processing",
  scans: "Scans",
  cleanup: "Cleanup",
  library: "Library",
  connections: "Connections",
  backups: "Backups",
  sign_in: "Sign-in",
  updates: "Updates",
  weir: "Weir",
};

/** The job statuses a log can be narrowed to, in the words a job's row uses. */
export const LOG_JOB_STATUSES: readonly { value: string; label: string }[] = [
  { value: "pending", label: "Queued" },
  { value: "leased", label: "Running" },
  { value: "completed", label: "Finished" },
  { value: "failed", label: "Failed" },
  { value: "cancelled", label: "Cancelled" },
  { value: "handler_ok_finalize_failed", label: "Recovery needed" },
];

const LEVELS: readonly SystemLogLevel[] = [
  "error",
  "warning",
  "info",
  "success",
];
const CATEGORIES = Object.keys(LOG_CATEGORY_LABELS) as SystemLogCategory[];
const SOURCES = LOG_SOURCES.map((source) => source.value);
const JOB_STATUSES = LOG_JOB_STATUSES.map((status) => status.value);

/** Everything that narrows the log, as the page holds it. The address holds the same, so a view can be linked to. */
export type LogFilters = {
  /** The one source the log is narrowed to; null is all of them. */
  source: SystemLogSource | null;
  levels: SystemLogLevel[];
  categories: SystemLogCategory[];
  workflow: number | null;
  when: LogWhen;
  /** The custom range's ends as a `datetime-local` input holds them, read in Weir's time zone. */
  from: string;
  to: string;
  text: string;
  /** What only some sources have: an event's type, result and trigger, a job's statuses, a server line's stack trace. */
  eventType: string;
  result: string;
  trigger: string;
  statuses: string[];
  stackOnly: boolean;
  /** One job, and what is recorded about it. */
  job: number | null;
};

export const EMPTY_LOG_FILTERS: LogFilters = {
  source: null,
  levels: [],
  categories: [],
  workflow: null,
  when: "any",
  from: "",
  to: "",
  text: "",
  eventType: "",
  result: "",
  trigger: "",
  statuses: [],
  stackOnly: false,
  job: null,
};

/** The address's names for the filters, so a page that leaves Logs can take them off the address. */
export const LOG_PARAMS = [
  "source",
  "level",
  "category",
  "workflow",
  "when",
  "from",
  "to",
  "q",
  "event_type",
  "result",
  "trigger",
  "status",
  "stack",
  "job",
  // Older addresses chose one of three lists with `show`.
  "show",
  "path",
] as const;

/** What an address's old `show` chose, as the source it stands for. */
const LEGACY_SHOW_SOURCES: Record<string, SystemLogSource> = {
  events: "event",
  activity: "event",
  jobs: "job",
  server: "server",
  log: "server",
};

function listParam<T extends string>(
  params: URLSearchParams,
  name: string,
  allowed: readonly T[],
): T[] {
  const named = (params.get(name) ?? "")
    .split(",")
    .map((value) => value.trim());
  return allowed.filter((value) => named.includes(value));
}

function numberParam(params: URLSearchParams, name: string): number | null {
  const value = Number(params.get(name));
  return Number.isInteger(value) && value > 0 ? value : null;
}

/**
 * The source an address names. A link may name several, as the log once took them: one valid name is that source, and
 * several or none is all of them, unless an older `show` says which list it meant.
 */
function sourceFromParams(params: URLSearchParams): SystemLogSource | null {
  const named = listParam(params, "source", SOURCES);
  if (named.length === 1) return named[0];
  if (named.length > 1) return null;
  return LEGACY_SHOW_SOURCES[params.get("show") ?? ""] ?? null;
}

/** The filters an address holds. An address from before the three lists became one log still lands where it meant. */
export function filtersFromParams(params: URLSearchParams): LogFilters {
  const when = LOG_WHEN_OPTIONS.find((o) => o.value === params.get("when"));
  return {
    source: sourceFromParams(params),
    levels: listParam(params, "level", LEVELS),
    categories: listParam(params, "category", CATEGORIES),
    workflow: numberParam(params, "workflow"),
    when: when?.value ?? "any",
    from: params.get("from") ?? "",
    to: params.get("to") ?? "",
    text: params.get("q")?.trim() ?? "",
    eventType: params.get("event_type") ?? "",
    result: params.get("result") ?? "",
    trigger: params.get("trigger") ?? "",
    statuses: listParam(params, "status", JOB_STATUSES),
    stackOnly: params.get("stack") === "1",
    job: numberParam(params, "job"),
  };
}

/** `base` with its Logs filters replaced by `filters`; every other part of the address stays. */
export function paramsFromFilters(
  filters: LogFilters,
  base: URLSearchParams,
): URLSearchParams {
  const params = new URLSearchParams(base);
  for (const name of LOG_PARAMS) params.delete(name);
  if (filters.source) params.set("source", filters.source);
  const lists: [string, readonly string[]][] = [
    ["level", filters.levels],
    ["category", filters.categories],
    ["status", filters.statuses],
  ];
  for (const [name, values] of lists) {
    if (values.length > 0) params.set(name, values.join(","));
  }
  const values: [string, string | null][] = [
    ["workflow", filters.workflow === null ? null : String(filters.workflow)],
    ["when", filters.when === "any" ? null : filters.when],
    ["from", filters.when === "custom" ? filters.from : null],
    ["to", filters.when === "custom" ? filters.to : null],
    ["q", filters.text.trim()],
    ["event_type", filters.eventType],
    ["result", filters.result],
    ["trigger", filters.trigger],
    ["stack", filters.stackOnly ? "1" : null],
    ["job", filters.job === null ? null : String(filters.job)],
  ];
  for (const [name, value] of values) {
    if (value) params.set(name, value);
  }
  return params;
}

const HOUR_MS = 60 * 60 * 1000;
const DAY_MS = 24 * HOUR_MS;
const RELATIVE_SPANS: Partial<Record<LogWhen, number>> = {
  hour: HOUR_MS,
  day: DAY_MS,
  week: 7 * DAY_MS,
};

/** The two instants a choice of time stands for, from `now`; either is undefined for an open end. */
export function rangeFor(
  filters: Pick<LogFilters, "when" | "from" | "to">,
  now: number,
  timeZone: string | undefined,
): { from?: string; to?: string } {
  const iso = (ms: number | null) =>
    ms === null ? undefined : new Date(ms).toISOString();
  const span = RELATIVE_SPANS[filters.when];
  if (span !== undefined) return { from: iso(now - span) };
  if (filters.when === "today")
    return { from: iso(startOfDayAt(now, timeZone)) };
  if (filters.when === "custom") {
    return {
      from: iso(instantFromLocalInput(filters.from, timeZone)),
      to: iso(instantFromLocalInput(filters.to, timeZone)),
    };
  }
  return {};
}

/** What the server is asked for. `now` is when the time was chosen, so a page of the log does not move as it ages. */
export function logQuery(
  filters: LogFilters,
  now: number,
  timeZone: string | undefined,
): SystemLogQuery {
  const query: SystemLogQuery = {};
  if (filters.source !== null) query.source = [filters.source];
  if (filters.levels.length > 0) query.level = filters.levels;
  if (filters.categories.length > 0) query.category = filters.categories;
  if (filters.statuses.length > 0) query.status = filters.statuses;
  if (filters.workflow !== null) query.workflow = filters.workflow;
  if (filters.job !== null) query.job = filters.job;
  const text = filters.text.trim();
  if (text) query.q = text;
  if (filters.eventType) query.event_type = filters.eventType;
  if (filters.result) query.result = filters.result;
  if (filters.trigger) query.trigger = filters.trigger;
  if (filters.stackOnly) query.has_exception = true;
  return { ...query, ...rangeFor(filters, now, timeZone) };
}

/** Whether anything narrows the log. */
export function anyLogFilterSet(filters: LogFilters): boolean {
  return paramsFromFilters(filters, new URLSearchParams()).toString() !== "";
}

/** How many of the filters the "Refine" row holds are set: the ones with no place in the header. */
export function refineCount(filters: LogFilters): number {
  return [
    filters.eventType,
    filters.result,
    filters.trigger,
    filters.statuses.length > 0,
    filters.stackOnly,
    filters.job !== null,
  ].filter(Boolean).length;
}
