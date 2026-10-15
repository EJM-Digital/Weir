/** A figure to the digits its unit shows: whole bytes, whole numbers from 100, then one decimal (two from GB). */
function figure(value: number, unit: number): string {
  return value >= 100 || unit === 0
    ? String(Math.round(value))
    : value.toFixed(unit >= 3 ? 2 : 1);
}

/**
 * One way to say a size, everywhere, so two screens that show the same number print it the same way.
 * Binary units, because that is what a disk reports, with whole numbers once a figure is big enough
 * for a decimal to be noise.
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes)) return "";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = Math.abs(bytes);
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  let shown = figure(value, unit);
  // A figure that rounds up to 1024 belongs to the next unit: 1023.96 KB is 1.0 MB.
  if (Number(shown) >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
    shown = figure(value, unit);
  }
  return `${bytes < 0 ? "-" : ""}${shown} ${units[unit]}`;
}
