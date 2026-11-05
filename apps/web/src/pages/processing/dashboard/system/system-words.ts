/** How the System view words what it reads: sizes, rates, spans, and the address a person types. */
import { formatBytes } from "../../../../lib/format/bytes";

const SECONDS_PER_MINUTE = 60;
const SECONDS_PER_HOUR = 3600;
const SECONDS_PER_DAY = 86_400;
const MEGABYTE = 1024 * 1024;
const GIGABYTE = MEGABYTE * 1024;
/** Bytes as gigabytes, the unit memory is shown in. */
export const gigabytes = (bytes: number): number => bytes / GIGABYTE;

/** A span of time as an uptime counts it, short enough for a small tile: "42:07" in the first hour, then "3h 05m", then "2d 4h". */
export function uptimeWords(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  if (whole < SECONDS_PER_HOUR) {
    const minutes = Math.floor(whole / SECONDS_PER_MINUTE);
    return `${minutes}:${String(whole % SECONDS_PER_MINUTE).padStart(2, "0")}`;
  }
  if (whole < SECONDS_PER_DAY) {
    const hours = Math.floor(whole / SECONDS_PER_HOUR);
    const minutes = Math.floor((whole % SECONDS_PER_HOUR) / SECONDS_PER_MINUTE);
    return `${hours}h ${String(minutes).padStart(2, "0")}m`;
  }
  const days = Math.floor(whole / SECONDS_PER_DAY);
  const hours = Math.floor((whole % SECONDS_PER_DAY) / SECONDS_PER_HOUR);
  return `${days}d ${hours}h`;
}

/** How long a computer has been up, to the unit that matters: "up 12 min", "up 7 h", "up 2 days". */
export function machineUpWords(seconds: number): string {
  const minutes = Math.floor(seconds / SECONDS_PER_MINUTE);
  if (minutes < 60) return `up ${Math.max(1, minutes)} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `up ${hours} h`;
  const days = Math.floor(hours / 24);
  return `up ${days} days`;
}

/** When a drive will be full, from how fast it is filling: "~12 days", "~3 months"; empty when it is not filling. */
export function fullInWords(days: number | null): string {
  if (days === null || !Number.isFinite(days) || days <= 0) return "";
  const rounded = Math.max(1, Math.round(days));
  if (rounded > 365) return "";
  if (rounded > 90) return `~${Math.round(rounded / 30)} months`;
  return `~${rounded} ${rounded === 1 ? "day" : "days"}`;
}

/** The host and port of an address: "http://192.0.2.5:9347/" is host "192.0.2.5", port "9347". */
export function splitAddress(address: string): {
  host: string;
  port: string | null;
} {
  const bare = address
    .trim()
    .replace(/^[a-z]+:\/\//i, "")
    .replace(/\/.*$/, "");
  const match = /^(.*):(\d+)$/.exec(bare);
  return match
    ? { host: match[1], port: match[2] }
    : { host: bare, port: null };
}

/** A size, or "0 B" for none. */
export const sizeWords = (bytes: number): string => formatBytes(bytes);
