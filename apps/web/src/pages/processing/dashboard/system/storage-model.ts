/** What the Storage card says: one block for each drive any workflow reads from or writes to. */
import type { SystemDrive } from "../../../../lib/system/system-stats-types";
import { formatRatePair } from "../../../../lib/format/rate";
import { fullInWords, sizeWords } from "./system-words";

export type DriveBlock = {
  key: string;
  name: string;
  path: string;
  /** "1.5 TB free · full in ~12 days". */
  freeLine: string;
  /** The names of the workflows that use the drive, for its tooltip. */
  workflows: string;
  /** What the stacked bar fills, as a share of the drive from 0 to 100: Weir's work files, then everything else. */
  weirShare: number;
  otherShare: number;
  /** Where the drive's keep-free line falls along the bar, or null when no workflow keeps any room free. */
  keepFreeAt: number | null;
  keepFreeLine: string;
  /** The drive has less room than a workflow insists on keeping. */
  low: boolean;
  /** "read 4.0 · write 12 MB/s · 18% busy"; empty where the drive's I/O cannot be read, as on a network share. */
  ioLine: string;
};

const PERCENT = 100;

/** A share of a drive from 0 to 100; 0 for a drive of no size. */
function shareOf(bytes: number, total: number): number {
  if (total <= 0) return 0;
  return Math.min(PERCENT, Math.max(0, (bytes / total) * PERCENT));
}

function ioLine(drive: SystemDrive): string {
  if (drive.read_bytes_per_sec === null || drive.write_bytes_per_sec === null)
    return "";
  const pair = formatRatePair(
    drive.read_bytes_per_sec,
    drive.write_bytes_per_sec,
  );
  const rates = `read ${pair.first} · write ${pair.second}`;
  return drive.busy_percent === null
    ? rates
    : `${rates} · ${Math.round(drive.busy_percent)}% busy`;
}

function freeLine(drive: SystemDrive): string {
  const full = fullInWords(drive.full_in_days);
  const free = `${sizeWords(drive.free_bytes)} free`;
  return full ? `${free} · full in ${full}` : free;
}

/** A block for each drive. */
export function driveBlocks(drives: readonly SystemDrive[]): DriveBlock[] {
  return drives.map((drive) => {
    const weir = Math.min(drive.weir_bytes, drive.total_bytes);
    const others = Math.max(0, drive.total_bytes - drive.free_bytes - weir);
    const keeps = drive.keep_free_bytes > 0 && drive.total_bytes > 0;
    return {
      key: `${drive.name}|${drive.path}`,
      name: drive.name,
      path: drive.path,
      freeLine: freeLine(drive),
      workflows: drive.workflows.map((workflow) => workflow.name).join(", "),
      weirShare: shareOf(weir, drive.total_bytes),
      otherShare: shareOf(others, drive.total_bytes),
      keepFreeAt: keeps
        ? PERCENT - shareOf(drive.keep_free_bytes, drive.total_bytes)
        : null,
      keepFreeLine: keeps
        ? `keeps ${sizeWords(drive.keep_free_bytes)} free`
        : "",
      low: keeps && drive.free_bytes < drive.keep_free_bytes,
      ioLine: ioLine(drive),
    };
  });
}

/** The card's quiet count: the room left across the drives shown, or nothing when there are none. */
export function storageCount(drives: readonly SystemDrive[]): string {
  if (drives.length === 0) return "";
  const free = drives.reduce((sum, drive) => sum + drive.free_bytes, 0);
  return `${sizeWords(free)} free`;
}
