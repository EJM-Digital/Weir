/**
 * The one way to say a speed, everywhere. Binary steps, the same base as `formatBytes`: a rate under 1 MB/s is whole
 * KB/s ("120 KB/s"); from 1 MB/s it is MB/s, to a tenth under 10 and whole from 10 ("3.4 MB/s", "42 MB/s"); from
 * 1000 MB/s it is GB/s, so a figure is never four digits wide ("1.1 GB/s").
 */
const KILOBYTE = 1024;
const MEGABYTE = KILOBYTE * 1024;
const GIGABYTE = MEGABYTE * 1024;
/** A figure of this or more is whole; under it, to a tenth. */
const WHOLE_FROM = 10;
/** A rate of this many MB/s or more is said in GB/s. */
const GIGABYTE_RATE_FROM_MB = 1000;

/** The unit of a rate, with the leading space a figure's unit is set after. */
export type RateUnit = " KB/s" | " MB/s" | " GB/s";

/** How many bytes a second one of each unit is. */
const RATE_UNIT_BYTES: Record<RateUnit, number> = {
  " KB/s": KILOBYTE,
  " MB/s": MEGABYTE,
  " GB/s": GIGABYTE,
};

const safe = (bytesPerSecond: number | null): number =>
  bytesPerSecond !== null && Number.isFinite(bytesPerSecond)
    ? Math.max(0, bytesPerSecond)
    : 0;

/** A count in MB/s or GB/s: a tenth under 10, whole from 10. */
const bigFigure = (value: number): string =>
  Number(value.toFixed(1)) < WHOLE_FROM
    ? value.toFixed(1)
    : Math.round(value).toString();

/** The unit a rate is said in, counting a figure that only rounds up to the next unit as already in it. */
function unitOf(bytesPerSecond: number): RateUnit {
  if (Math.round(bytesPerSecond / KILOBYTE) < KILOBYTE) return " KB/s";
  return Math.round(bytesPerSecond / MEGABYTE) < GIGABYTE_RATE_FROM_MB
    ? " MB/s"
    : " GB/s";
}

/** A rate as a figure in the given unit, without the unit: "120", "3.4", "42". */
function figureIn(bytesPerSecond: number, unit: RateUnit): string {
  const value = safe(bytesPerSecond) / RATE_UNIT_BYTES[unit];
  return unit === " KB/s" ? Math.round(value).toString() : bigFigure(value);
}

/** A rate with its unit: "120 KB/s", "3.4 MB/s", "42 MB/s", "1.1 GB/s". */
export function formatRate(bytesPerSecond: number): string {
  const bytes = safe(bytesPerSecond);
  const unit = unitOf(bytes);
  return `${figureIn(bytes, unit)}${unit}`;
}

/**
 * How a column of figures is said when it counts in one unit, the one its largest value needs: the figure of any
 * value in that unit, and the unit. Without a value the unit is KB/s.
 */
export function rateScale(largestBytesPerSecond: number | null): {
  figure: (bytesPerSecond: number) => string;
  unit: RateUnit;
} {
  const unit = unitOf(safe(largestBytesPerSecond));
  return { figure: (bytes) => figureIn(bytes, unit), unit };
}

/**
 * Two rates said side by side. When both are in one unit it is said once, after the second ("4.0", "12 MB/s");
 * when they differ each carries its own ("120 KB/s", "12 MB/s").
 */
export function formatRatePair(
  first: number,
  second: number,
): { first: string; second: string } {
  const a = safe(first);
  const b = safe(second);
  const unit = unitOf(a);
  return unit === unitOf(b)
    ? { first: figureIn(a, unit), second: `${figureIn(b, unit)}${unit}` }
    : { first: formatRate(a), second: formatRate(b) };
}
