/**
 * When the next automatic configuration backup runs, by the rule the server applies (ConfigurationBackupSchedule): a
 * backup is due once the interval has passed since the last one, and when the interval is a day or more, only on a
 * new calendar day and not before the preferred time of day, in the timezone chosen in Settings.
 */
import { dayKey } from "./system-time";

const HOUR_MS = 3_600_000;
const DAY_MS = 24 * HOUR_MS;
const MIN_INTERVAL_MS = HOUR_MS;
const MAX_INTERVAL_MS = 30 * DAY_MS;
const HOURS_PER_DAY = 24;
const DEFAULT_INTERVAL_HOURS = 24;
const DEFAULT_TIME = { hour: 2, minute: 0 };
/** Days looked ahead for a day the rule allows: the day after the last backup is always one. */
const DAYS_LOOKED_AHEAD = 3;

export type BackupRun = {
  enabled: boolean;
  intervalHours: number;
  /** "HH:MM", 24-hour. */
  preferredTime: string;
  /** When the last scheduled backup ran, in ms since the epoch. Null if none has. */
  lastRunAt: number | null;
};

function timeOfDay(value: string): { hour: number; minute: number } {
  const match = /^(\d{1,2}):(\d{1,2})$/.exec(value.trim());
  if (!match) return DEFAULT_TIME;
  const hour = Number(match[1]);
  const minute = Number(match[2]);
  return hour <= 23 && minute <= 59 ? { hour, minute } : DEFAULT_TIME;
}

/** How far a timezone is ahead of UTC at an instant, in ms. */
function offsetAt(ms: number, timeZone: string | undefined): number {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone,
    hourCycle: "h23",
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "numeric",
    minute: "numeric",
    second: "numeric",
  }).formatToParts(ms);
  const part = (type: string) =>
    Number(parts.find((entry) => entry.type === type)?.value);
  const wall = Date.UTC(
    part("year"),
    part("month") - 1,
    part("day"),
    part("hour"),
    part("minute"),
    part("second"),
  );
  return wall - Math.floor(ms / 1000) * 1000;
}

/** The instant a wall-clock time on a calendar day ("2026-10-02") falls at, in a timezone. */
function instantOf(
  day: string,
  { hour, minute }: { hour: number; minute: number },
  timeZone: string | undefined,
): number {
  const [year, month, date] = day.split("-").map(Number);
  const asUtc = Date.UTC(year, month - 1, date, hour, minute);
  // The offset is read at the guess, then again at the answer, so a day that changes offset still lands on the clock time.
  const first = asUtc - offsetAt(asUtc, timeZone);
  return asUtc - offsetAt(first, timeZone);
}

function nextDay(day: string): string {
  const [year, month, date] = day.split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, date + 1))
    .toISOString()
    .slice(0, 10);
}

function safeOffsetZone(timeZone: string | undefined): string | undefined {
  try {
    offsetAt(0, timeZone);
    return timeZone;
  } catch {
    return undefined;
  }
}

/**
 * When the next automatic backup runs, in ms since the epoch. Null when automatic backups are off. A time that has
 * already passed means the backup is due now: the server checks once a minute.
 */
export function nextBackupAt(
  run: BackupRun,
  now: number,
  timeZone: string | undefined,
): number | null {
  if (!run.enabled) return null;
  const zone = safeOffsetZone(timeZone);
  const hours =
    run.intervalHours > 0 ? run.intervalHours : DEFAULT_INTERVAL_HOURS;
  const intervalMs = Math.min(
    MAX_INTERVAL_MS,
    Math.max(MIN_INTERVAL_MS, hours * HOUR_MS),
  );
  const earliest = run.lastRunAt === null ? now : run.lastRunAt + intervalMs;
  if (hours < HOURS_PER_DAY) return earliest;
  const lastDay = run.lastRunAt === null ? null : dayKey(run.lastRunAt, zone);
  const time = timeOfDay(run.preferredTime);
  let day = dayKey(Math.max(earliest, now), zone);
  for (let looked = 0; looked < DAYS_LOOKED_AHEAD; looked++) {
    if (day !== lastDay) {
      const target = Math.max(instantOf(day, time, zone), earliest);
      /* istanbul ignore else -- @preserve */
      if (dayKey(target, zone) === day) return target;
    }
    day = nextDay(day);
  }
  return earliest;
}
