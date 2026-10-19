import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";

/** The server's grid: 7 days of 96 quarter-hours, Monday first (ScheduleGrid.cs). */
export const SLOTS_PER_HOUR = 4;
export const HOURS_PER_DAY = 24;
export const SLOTS_PER_DAY = HOURS_PER_DAY * SLOTS_PER_HOUR;
export const SLOTS_PER_WEEK = 7 * SLOTS_PER_DAY;
export const DAY_NAMES = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
export const DAY_FULL_NAMES = [
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
  "Sunday",
];

function usable(grid: string): boolean {
  return grid.length === SLOTS_PER_WEEK && !/[^01]/.test(grid);
}

/**
 * A grid the editor can change. Anything unusable is "no restriction", which is what the server does
 * with it too: an editor that invented a schedule would be worse than one that shows none.
 */
function editable(grid: string): string {
  return usable(grid) ? grid : "1".repeat(SLOTS_PER_WEEK);
}

/**
 * Whether two grids draw the same week in the editor. A week with every hour on looks the same as no restriction, so a
 * person who draws a change and then undoes it has nothing to save.
 */
export function sameWeek(a: string, b: string): boolean {
  return editable(a) === editable(b);
}

/** Whether an hour is on in the editor: it is when any of its quarters is. */
export function hourIsOn(grid: string, day: number, hour: number): boolean {
  const start = day * SLOTS_PER_DAY + hour * SLOTS_PER_HOUR;
  return editable(grid)
    .slice(start, start + SLOTS_PER_HOUR)
    .includes("1");
}

/** The grid with one hour switched on or off: all four of its quarters, so nothing is lost in the round trip. */
export function withHour(
  grid: string,
  day: number,
  hour: number,
  on: boolean,
): string {
  const base = editable(grid);
  const start = day * SLOTS_PER_DAY + hour * SLOTS_PER_HOUR;
  const quarters = (on ? "1" : "0").repeat(SLOTS_PER_HOUR);
  return `${base.slice(0, start)}${quarters}${base.slice(start + SLOTS_PER_HOUR)}`;
}

function hhmm(text: string, fallback: [number, number]): [number, number] {
  const match = /^\s*(\d{1,2}):(\d{1,2})\s*$/.exec(text);
  if (!match) return fallback;
  const hour = Number(match[1]);
  const minute = Number(match[2]);
  if (hour > 23 || minute > 59) return fallback;
  return [hour, minute];
}

/**
 * The grid a days/start/end window describes, as `ScheduleGrid.FromDaysAndTimes` builds it. Libraries saved before
 * the grid existed still carry one of these, and admission still honours it, so the week shown must too.
 */
export function gridFromDaysAndTimes(
  days: string,
  start: string,
  end: string,
): string {
  const wanted = new Set(
    days
      .split(",")
      .map((d) => d.trim().toLowerCase().slice(0, 3))
      .filter((d) => d.length > 0),
  );
  if (wanted.size === 0) return "";
  const [sh, sm] = hhmm(start || "00:00", [0, 0]);
  const [eh, em] = hhmm(end || "23:59", [23, 59]);
  const startSlot = sh * SLOTS_PER_HOUR + Math.floor(sm / 15);
  const endSlot = eh * SLOTS_PER_HOUR + Math.floor(em / 15);
  const slots = Array.from({ length: SLOTS_PER_WEEK }, () => "0");
  DAY_NAMES.forEach((name, day) => {
    if (!wanted.has(name.toLowerCase())) return;
    const base = day * SLOTS_PER_DAY;
    if (startSlot <= endSlot) {
      for (let s = startSlot; s <= endSlot; s += 1) slots[base + s] = "1";
    } else {
      for (let s = startSlot; s < SLOTS_PER_DAY; s += 1) slots[base + s] = "1";
      const next = ((day + 1) % 7) * SLOTS_PER_DAY;
      for (let s = 0; s <= endSlot; s += 1) slots[next + s] = "1";
    }
  });
  return slots.join("");
}

type ScheduleFields = Pick<
  ProcessingLibrary,
  | "schedule_enabled"
  | "schedule_grid"
  | "schedule_hours_limited"
  | "schedule_days"
  | "schedule_start"
  | "schedule_end"
>;

/**
 * The week a library actually runs to, as `WorkAdmissionRules.LibraryWindowOpen` decides it: a drawn grid wins, then
 * the older days-and-hours window, else any time. Empty means any time.
 */
export function effectiveGrid(library: ScheduleFields): string {
  if (!library.schedule_enabled) return "";
  const grid = (library.schedule_grid ?? "").trim();
  if (grid.length > 0) return usable(grid) ? grid : "";
  if (!library.schedule_hours_limited) return "";
  return gridFromDaysAndTimes(
    library.schedule_days ?? "",
    library.schedule_start ?? "",
    library.schedule_end ?? "",
  );
}

/** Which of the 168 hours are on: an hour counts when any of its quarters is. */
export function weekHours(grid: string): boolean[][] {
  return DAY_NAMES.map((_, day) =>
    Array.from({ length: HOURS_PER_DAY }, (_, hour) =>
      hourIsOn(grid, day, hour),
    ),
  );
}

/** The wall clock in the app's zone: Monday is 0, as the server counts. */
export function zoneClock(
  now: Date,
  timeZone: string | undefined,
): { weekday: number; hour: number; minute: number } {
  let parts: Intl.DateTimeFormatPart[];
  try {
    parts = new Intl.DateTimeFormat("en-GB", {
      timeZone: timeZone || "UTC",
      weekday: "short",
      hour: "2-digit",
      minute: "2-digit",
      hourCycle: "h23",
    }).formatToParts(now);
  } catch {
    parts = new Intl.DateTimeFormat("en-GB", {
      timeZone: "UTC",
      weekday: "short",
      hour: "2-digit",
      minute: "2-digit",
      hourCycle: "h23",
    }).formatToParts(now);
  }
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  return {
    weekday: Math.max(0, DAY_NAMES.indexOf(get("weekday").slice(0, 3))),
    hour: Number(get("hour")) % 24,
    minute: Number(get("minute")),
  };
}

function slotWords(slot: number): string {
  const day = DAY_NAMES[Math.floor(slot / SLOTS_PER_DAY)];
  const inDay = slot % SLOTS_PER_DAY;
  const hour = String(Math.floor(inDay / SLOTS_PER_HOUR)).padStart(2, "0");
  const minute = String((inDay % SLOTS_PER_HOUR) * 15).padStart(2, "0");
  return `${day} ${hour}:${minute}`;
}

export type WindowNow =
  | { kind: "any" }
  | { kind: "never" }
  | { kind: "open"; until: string | null }
  | { kind: "closed"; opens: string };

/** Whether the library may start work right now, and when that changes. */
export function windowNow(
  grid: string,
  timeZone: string | undefined,
  now: Date,
): WindowNow {
  if (!usable(grid)) return { kind: "any" };
  if (!grid.includes("1")) return { kind: "never" };
  const clock = zoneClock(now, timeZone);
  const slot =
    clock.weekday * SLOTS_PER_DAY +
    clock.hour * SLOTS_PER_HOUR +
    Math.floor(clock.minute / 15);
  const open = grid[slot] === "1";
  for (let ahead = 1; ahead <= SLOTS_PER_WEEK; ahead += 1) {
    const next = (slot + ahead) % SLOTS_PER_WEEK;
    if ((grid[next] === "1") !== open) {
      return open
        ? { kind: "open", until: slotWords(next) }
        : { kind: "closed", opens: slotWords(next) };
    }
  }
  return { kind: "open", until: null };
}

/** The configuration backup's timer in words, as `ConfigurationBackupSchedule.IsDue` runs it. */
export function backupWords(
  enabled: boolean,
  intervalHours: number,
  preferredTime: string,
): string {
  if (!enabled) return "Off";
  const hours = intervalHours > 0 ? intervalHours : 24;
  const at = preferredTime.trim() || "02:00";
  if (hours === 24) return `Every day at ${at}`;
  if (hours > 24 && hours % 24 === 0)
    return `Every ${hours / 24} days at ${at}`;
  return hours === 1 ? "Every hour" : `Every ${hours} hours`;
}
