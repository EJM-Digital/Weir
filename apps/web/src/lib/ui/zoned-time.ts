/** Wall-clock times in a chosen time zone: the zone chosen in System › About, or the browser's when none is set. */

const MS_PER_SECOND = 1000;
const MS_PER_DAY = 24 * 60 * 60 * MS_PER_SECOND;

/** A wall-clock reading, with the month counted from 1. */
export type WallClock = {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
};

/** How far the zone's wall clock is ahead of UTC at an instant, in ms. */
function offsetAt(ms: number, timeZone: string): number {
  const clock = new Intl.DateTimeFormat("en-US", {
    timeZone,
    hourCycle: "h23",
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "numeric",
    minute: "numeric",
    second: "numeric",
  });
  const part = (type: Intl.DateTimeFormatPartTypes) =>
    Number(clock.formatToParts(ms).find((p) => p.type === type)?.value);
  const shown = Date.UTC(
    part("year"),
    part("month") - 1,
    part("day"),
    part("hour"),
    part("minute"),
    part("second"),
  );
  return shown - Math.floor(ms / MS_PER_SECOND) * MS_PER_SECOND;
}

/** The instant the wall clock of `timeZone` reads `clock`; for a reading that happens twice or never, the nearest one. */
export function instantAtWallClock(
  clock: WallClock,
  timeZone: string | undefined,
): number {
  const { year, month, day, hour, minute } = clock;
  if (!timeZone) return new Date(year, month - 1, day, hour, minute).getTime();
  try {
    const asUtc = Date.UTC(year, month - 1, day, hour, minute);
    const first = asUtc - offsetAt(asUtc, timeZone);
    return asUtc - offsetAt(first, timeZone);
  } catch {
    // An unknown zone name: read the clock as the browser's rather than not at all.
    return new Date(year, month - 1, day, hour, minute).getTime();
  }
}

/** The calendar day an instant falls on in `timeZone`. */
function dayOf(
  ms: number,
  timeZone: string | undefined,
): Pick<WallClock, "year" | "month" | "day"> {
  const date = new Date(ms);
  try {
    const parts = new Intl.DateTimeFormat("en-CA", {
      timeZone,
      year: "numeric",
      month: "numeric",
      day: "numeric",
    }).formatToParts(date);
    const part = (type: Intl.DateTimeFormatPartTypes) =>
      Number(parts.find((p) => p.type === type)?.value);
    return { year: part("year"), month: part("month"), day: part("day") };
  } catch {
    return {
      year: date.getFullYear(),
      month: date.getMonth() + 1,
      day: date.getDate(),
    };
  }
}

/** The instant the day `ms` falls on began, in `timeZone`. */
export function startOfDayAt(ms: number, timeZone: string | undefined): number {
  return instantAtWallClock(
    { ...dayOf(ms, timeZone), hour: 0, minute: 0 },
    timeZone,
  );
}

/** Whether the reading names a minute that exists on the calendar: no 13th month, no 31 April, no 25 o'clock. */
function isRealWallClock({ year, month, day, hour, minute }: WallClock) {
  const date = new Date(Date.UTC(2000, month - 1, day));
  date.setUTCFullYear(year);
  return (
    hour <= 23 &&
    minute <= 59 &&
    date.getUTCFullYear() === year &&
    date.getUTCMonth() === month - 1 &&
    date.getUTCDate() === day
  );
}

const LOCAL_INPUT = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/;

/** The instant a `datetime-local` value (such as "2026-10-02T08:30") stands for in `timeZone`, or null when it is not one. */
export function instantFromLocalInput(
  value: string,
  timeZone: string | undefined,
): number | null {
  const match = LOCAL_INPUT.exec(value);
  if (!match) return null;
  const [year, month, day, hour, minute] = match.slice(1).map(Number);
  if (!isRealWallClock({ year, month, day, hour, minute })) return null;
  return instantAtWallClock({ year, month, day, hour, minute }, timeZone);
}

const twoDigits = (value: number) => String(value).padStart(2, "0");

/** The calendar day an instant falls on in the zone, as "2026-10-02". */
export function dayKeyAt(ms: number, timeZone: string | undefined): string {
  const { year, month, day } = dayOf(ms, timeZone);
  return `${year}-${twoDigits(month)}-${twoDigits(day)}`;
}

/** The day before the one an instant falls on, as "2026-10-01"; half a day back from the day's start is always inside it. */
export function previousDayKeyAt(
  ms: number,
  timeZone: string | undefined,
): string {
  return dayKeyAt(startOfDayAt(ms, timeZone) - MS_PER_DAY / 2, timeZone);
}
