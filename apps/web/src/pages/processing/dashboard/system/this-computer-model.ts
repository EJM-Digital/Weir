/** What the This computer card says: three readings with their traces, and the machine's own tags. */
import type { TraceSample } from "../../../../components/charts/live-trace-math";
import { formatBytes } from "../../../../lib/format/bytes";
import {
  formatRate,
  formatRatePair,
  rateScale,
} from "../../../../lib/format/rate";
import { seriesOf } from "../../../../lib/system/system-stats-model";
import type {
  SystemNow,
  SystemStats,
} from "../../../../lib/system/system-stats-types";
import { narrowing, type Words } from "../../../../lib/ui/fit-text";
import type { StatusMeaning } from "../../../../lib/ui/status-meaning";
import { gigabytes, machineUpWords } from "./system-words";

export type ComputerKey = "cpu" | "memory" | "disk";

/** The scale a column's trace is drawn on: a fixed top for a percentage, or a rate that finds its own. */
export type ComputerScale =
  { kind: "percent" } | { kind: "rate"; floorTop: number };

export type ComputerColumn = {
  key: ComputerKey;
  label: string;
  /** The trace's colour, a design token. */
  colour: string;
  /** The big figure, or null when the machine cannot read it. */
  value: number | null;
  /** Writes the figure as it counts. */
  figure: (value: number) => string;
  unit: string;
  /** The line of detail, in words that narrow with the room (the fullest first), the least important part dropped first. */
  sub: Words;
  samples: TraceSample[];
  scale: ComputerScale;
  /** Writes a value of the trace for its readout. */
  readout: (value: number) => string;
};

const wholeFigure = (value: number) => Math.round(value).toString();
const tenthsFigure = (value: number) => value.toFixed(1);
const percentReadout = (value: number) => `${Math.round(value)}%`;

/** A disk trace is never scaled tighter than this, in bytes a second (1 MB/s), so an idle disk reads as idle. */
const DISK_FLOOR = 1024 * 1024;

/** Reads and writes together, in bytes a second; null unless both can be read, so a trace shows a gap rather than a made-up zero. */
export function diskBytesPerSecond(
  read: number | null,
  write: number | null,
): number | null {
  if (read === null || write === null) return null;
  return read + write;
}

/** The parts that have a reading, as the words of a line that narrows as the least important is dropped. */
function detail(
  parts: readonly { text: string | null; matters: number }[],
): Words {
  return narrowing(
    parts.flatMap((part) =>
      part.text === null ? [] : [{ text: part.text, matters: part.matters }],
    ),
  );
}

/** Weir's own share and the tools' matter most (what is using the machine); how many cores it has matters least. */
function cpuSub(now: SystemNow): Words {
  return detail([
    { text: `${now.cores} cores`, matters: 1 },
    {
      text:
        now.weir_cpu_percent === null
          ? null
          : `Weir ${Math.round(now.weir_cpu_percent)}%`,
      matters: 2,
    },
    {
      text:
        now.tools_cpu_percent === null
          ? null
          : `ffmpeg ${Math.round(now.tools_cpu_percent)}%`,
      matters: 3,
    },
  ]);
}

function memorySub(now: SystemNow): Words {
  return detail([
    {
      text:
        now.memory_total_bytes === null
          ? null
          : `of ${gigabytes(now.memory_total_bytes).toFixed(1)} GB`,
      matters: 1,
    },
    { text: `Weir ${formatBytes(now.weir_memory_bytes)}`, matters: 2 },
  ]);
}

function diskSub(now: SystemNow): Words {
  const { disk_read_bytes_per_sec: read, disk_write_bytes_per_sec: write } =
    now;
  if (read === null || write === null) return ["not available"];
  const full = `read ${formatRate(read)} · write ${formatRate(write)}`;
  const shared = formatRatePair(read, write);
  const split = [full, `R ${shared.first} · W ${shared.second}`];
  if (now.disk_busy_percent === null) return split;
  const busy = `${Math.round(now.disk_busy_percent)}% busy`;
  return [`${split[0]} · ${busy}`, ...split, busy];
}

/** The three columns, CPU, memory and disk, each with its reading now and its last ten minutes. */
export function computerColumns(stats: SystemStats): ComputerColumn[] {
  const { now, history } = stats;
  const disk = diskBytesPerSecond(
    now.disk_read_bytes_per_sec,
    now.disk_write_bytes_per_sec,
  );
  return [
    {
      key: "cpu",
      label: "CPU",
      colour: "var(--mm-lane-queued)",
      value: now.cpu_percent,
      figure: wholeFigure,
      unit: "%",
      sub: cpuSub(now),
      samples: seriesOf(history, (point) => point.cpu_percent),
      scale: { kind: "percent" },
      readout: percentReadout,
    },
    {
      key: "memory",
      label: "Memory",
      colour: "var(--mm-lane-analysing)",
      value:
        now.memory_used_bytes === null
          ? null
          : gigabytes(now.memory_used_bytes),
      figure: tenthsFigure,
      unit: " GB",
      sub: memorySub(now),
      samples: seriesOf(history, (point) => point.memory_percent),
      scale: { kind: "percent" },
      readout: percentReadout,
    },
    {
      key: "disk",
      label: "Disk",
      colour: "var(--mm-lane-incoming)",
      value: disk,
      ...rateScale(disk),
      sub: diskSub(now),
      samples: seriesOf(history, (point) =>
        diskBytesPerSecond(
          point.disk_read_bytes_per_sec,
          point.disk_write_bytes_per_sec,
        ),
      ),
      scale: { kind: "rate", floorTop: DISK_FLOOR },
      readout: formatRate,
    },
  ];
}

export type MachineTag = { text: string; meaning?: StatusMeaning };

/** The pills under the columns: the operating system, how long it has been up, and a reboot waiting for a person. */
export function machineTags(machine: SystemStats["machine"]): MachineTag[] {
  const tags: MachineTag[] = [];
  if (machine.os) tags.push({ text: machine.os });
  if (machine.uptime_seconds !== null)
    tags.push({ text: machineUpWords(machine.uptime_seconds) });
  if (machine.reboot_pending)
    tags.push({ text: "reboot pending", meaning: "attention" });
  return tags;
}
