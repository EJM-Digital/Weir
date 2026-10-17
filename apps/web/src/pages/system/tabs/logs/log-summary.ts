import { plural } from "../../../../lib/ui/mm-plural";

/** What the Log card's header says about the list under it, from what the list holds. */
export type LogSummaryInput = {
  /** Rows in hand. */
  loaded: number;
  /** Rows the filters match, loaded or not. */
  total: number;
  filtered: boolean;
  /** The live link to Weir is down, so the list is no longer following it. */
  paused: boolean;
};

/**
 * One quiet line for the Log card: how many entries, and whether the list is live. "212 entries · live", "Showing 50 of 212
 * entries · live", "3 entries matching · live"; while the live link is down, "212 entries · live updates paused".
 */
export function logSummaryWords({
  loaded,
  total,
  filtered,
  paused,
}: LogSummaryInput): string {
  const count = plural(total, "entry", "entries");
  const held =
    loaded < total
      ? `Showing ${loaded.toLocaleString()} of ${count}`
      : filtered
        ? `${count} matching`
        : count;
  return `${held} · ${paused ? "live updates paused" : "live"}`;
}
