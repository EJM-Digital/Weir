/** What the Processing card says: Weir's own disk work and speed over the last ten minutes, and what it finished. */
import type { TraceSample } from "../../../../components/charts/live-trace-math";
import { formatRate, rateScale } from "../../../../lib/format/rate";
import { seriesOf } from "../../../../lib/system/system-stats-model";
import type { SystemStats } from "../../../../lib/system/system-stats-types";
import type { Words } from "../../../../lib/ui/fit-text";
import { sizeWords } from "./system-words";

/** The window the card's finished-work figures cover, in minutes: the same ten minutes the traces show. */
export const RECENT_MINUTES = 10;

/** A disk trace is never scaled tighter than this, in bytes a second (1 MB/s), so quiet work reads as quiet. */
const DISK_FLOOR = 1024 * 1024;
/** A speed trace is never scaled tighter than this, in times real time. */
const SPEED_FLOOR = 10;
/** A speed of this or more reads as a whole number. */
const WHOLE_SPEED_FROM = 10;

export type TraceLine = {
  key: string;
  label: string;
  colour: string;
  samples: TraceSample[];
};

export type ProcessingColumn = {
  key: "disk" | "speed";
  label: string;
  colour: string;
  /** The big figure, or null when there is none to show. */
  value: number | null;
  /** Writes the figure as it counts. */
  figure: (value: number) => string;
  unit: string;
  /** The line of detail, in words that narrow with the room, the fullest first. */
  sub: Words;
  lines: TraceLine[];
  floorTop: number;
  /** Writes a value of the trace for its readout. */
  readout: (value: number) => string;
};

/** A speed as a person reads it: "0.8", "35", "1,260". */
export function speedFigure(times: number): string {
  if (times >= WHOLE_SPEED_FROM)
    return Math.round(times).toLocaleString("en-US");
  return times.toFixed(1).replace(/\.0$/, "");
}

export type RecentWork = {
  /** Files Weir finished in the last ten minutes. */
  done: number;
  /** The space those saved, in bytes. */
  savedBytes: number;
};

/**
 * The card's two columns: what the running passes read and write, and how fast they go. Their lines under the figures
 * also carry what the last ten minutes finished and saved, and how many passes run of the slots Weir has.
 */
export function processingColumns(
  stats: SystemStats,
  recent: RecentWork | undefined,
): ProcessingColumn[] {
  const { now, history } = stats;
  const saved = recent ? `${sizeWords(recent.savedBytes)} saved` : null;
  const done = recent ? `${recent.done.toLocaleString()} done` : null;
  const running = `${now.running} of ${now.slots} running`;
  const written = now.processing_write_bytes_per_sec;
  return [
    {
      key: "disk",
      label: "Disk work",
      colour: "var(--mm-lane-processing)",
      value: written,
      ...rateScale(written),
      sub: (() => {
        const read = formatRate(now.processing_read_bytes_per_sec);
        return saved
          ? [`reading ${read} · ${saved}`, saved, `read ${read}`]
          : [`writing · reading ${read}`, `reading ${read}`, `read ${read}`];
      })(),
      lines: [
        {
          key: "read",
          label: "read",
          colour: "var(--mm-lane-queued)",
          samples: seriesOf(
            history,
            (point) => point.processing_read_bytes_per_sec,
          ),
        },
        {
          key: "write",
          label: "write",
          colour: "var(--mm-lane-processing)",
          samples: seriesOf(
            history,
            (point) => point.processing_write_bytes_per_sec,
          ),
        },
      ],
      floorTop: DISK_FLOOR,
      readout: formatRate,
    },
    {
      key: "speed",
      label: "Speed",
      colour: "var(--mm-lane-delivering)",
      value: now.running === 0 ? null : now.processing_speed,
      figure: speedFigure,
      unit: "×",
      sub:
        now.running === 0
          ? done
            ? [`idle · ${done}`, "idle"]
            : ["nothing running", "idle"]
          : done
            ? [`${running} · ${done}`, running, `${now.running}/${now.slots}`]
            : [running, `${now.running}/${now.slots}`],
      lines: [
        {
          key: "speed",
          label: "speed",
          colour: "var(--mm-lane-delivering)",
          samples: seriesOf(history, (point) => point.processing_speed),
        },
      ],
      floorTop: SPEED_FLOOR,
      readout: (value) => `${speedFigure(value)}×`,
    },
  ];
}
