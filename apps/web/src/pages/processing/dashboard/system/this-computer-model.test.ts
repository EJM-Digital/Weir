import { describe, expect, it } from "vitest";

import type {
  SystemNow,
  SystemPoint,
  SystemStats,
} from "../../../../lib/system/system-stats-types";
import {
  computerColumns,
  diskMegabytes,
  machineTags,
} from "./this-computer-model";

const MB = 1024 * 1024;
const GB = 1024 * MB;

const now: SystemNow = {
  at: "2026-10-02T12:00:00Z",
  cpu_percent: 37.4,
  cores: 16,
  memory_used_bytes: 11 * GB,
  memory_total_bytes: 32 * GB,
  disk_read_bytes_per_sec: 4 * MB,
  disk_write_bytes_per_sec: 12 * MB,
  disk_busy_percent: 18,
  weir_cpu_percent: 3.1,
  weir_memory_bytes: 255 * MB,
  tools_cpu_percent: 41,
  processing_read_bytes_per_sec: 0,
  processing_write_bytes_per_sec: 0,
  processing_speed: 0,
  running: 2,
  slots: 4,
};

const point = (
  at: string,
  changes: Partial<SystemPoint> = {},
): SystemPoint => ({
  at,
  cpu_percent: 10,
  memory_percent: 30,
  disk_read_bytes_per_sec: MB,
  disk_write_bytes_per_sec: MB,
  processing_read_bytes_per_sec: 0,
  processing_write_bytes_per_sec: 0,
  processing_speed: 0,
  ...changes,
});

const stats = (changes: Partial<SystemNow> = {}): SystemStats => ({
  interval_ms: 1000,
  window_s: 600,
  now: { ...now, ...changes },
  history: [
    point("2026-10-02T11:59:58Z"),
    point("2026-10-02T11:59:59Z", { cpu_percent: 20 }),
  ],
  machine: {
    os: "Windows 11 Pro",
    uptime_seconds: 3 * 86_400,
    reboot_pending: false,
  },
  drives: [],
});

const column = (key: string, changes: Partial<SystemNow> = {}) =>
  computerColumns(stats(changes)).find((c) => c.key === key)!;

describe("the CPU column", () => {
  it("shows the load now with the cores and the shares of Weir and ffmpeg", () => {
    expect(column("cpu")).toMatchObject({
      value: 37.4,
      unit: "%",
      scale: { kind: "percent" },
    });
    expect(column("cpu").sub[0]).toBe("16 cores · Weir 3% · ffmpeg 41%");
  });

  it("drops the cores first for a narrower column, then Weir's share, and last of all says one share alone", () => {
    expect(column("cpu").sub).toEqual([
      "16 cores · Weir 3% · ffmpeg 41%",
      "Weir 3% · ffmpeg 41%",
      "ffmpeg 41%",
      "Weir 3%",
      "16 cores",
    ]);
  });

  it("leaves out a share the machine cannot read", () => {
    expect(
      column("cpu", { weir_cpu_percent: null, tools_cpu_percent: null }).sub,
    ).toEqual(["16 cores"]);
  });

  it("traces the history at each point's own time", () => {
    const samples = column("cpu").samples;
    expect(samples.map((sample) => sample.value)).toEqual([10, 20]);
    expect(samples[1].at - samples[0].at).toBe(1000);
  });
});

describe("a big disk figure", () => {
  it("counts and reads in GB/s from a thousand MB/s, in the figure, the unit and the trace's readout", () => {
    const big = column("disk", {
      disk_read_bytes_per_sec: 700 * MB,
      disk_write_bytes_per_sec: 427 * MB,
    });

    expect(big.value).toBe(1127);
    expect(big.unit).toBe(" GB/s");
    expect(big.figure(big.value ?? 0)).toBe("1.1");
    expect(big.readout(1127)).toBe("1.1 GB/s");
  });
});

describe("the memory column", () => {
  it("shows gigabytes used, of the total, with the share Weir holds", () => {
    expect(column("memory")).toMatchObject({
      value: 11,
      unit: " GB",
    });
  });

  it("drops the total first for a narrower column, keeping Weir's own share", () => {
    expect(column("memory").sub).toEqual([
      "of 32.0 GB · Weir 255 MB",
      "Weir 255 MB",
      "of 32.0 GB",
    ]);
  });
});

describe("the memory column when the machine does not say", () => {
  it("is a gap, not a made-up zero, when the memory in use cannot be read", () => {
    expect(column("memory", { memory_used_bytes: null }).value).toBeNull();
  });

  it("says only Weir's share when the total cannot be read", () => {
    expect(column("memory", { memory_total_bytes: null }).sub).toEqual([
      "Weir 255 MB",
    ]);
  });
});

describe("the disk column", () => {
  it("leaves out the busy time when the machine does not give one", () => {
    expect(column("disk", { disk_busy_percent: null }).sub).toEqual([
      "read 4.0 · write 12",
      "R 4.0 · W 12",
    ]);
  });

  it("adds reads and writes into one figure, with busy time in the note", () => {
    expect(column("disk")).toMatchObject({
      value: 16,
      unit: " MB/s",
      scale: { kind: "rate" },
    });
  });

  it("drops the busy time, then the words, for a narrower column, and lastly says only how busy", () => {
    expect(column("disk").sub).toEqual([
      "read 4.0 · write 12 · 18% busy",
      "read 4.0 · write 12",
      "R 4.0 · W 12",
      "18% busy",
    ]);
  });

  it("says it is not available where the machine gives no disk figures", () => {
    const unreadable = column("disk", {
      disk_read_bytes_per_sec: null,
      disk_write_bytes_per_sec: null,
      disk_busy_percent: null,
    });
    expect(unreadable.value).toBeNull();
    expect(unreadable.sub).toEqual(["not available"]);
  });

  it("is a gap, not a made-up zero, when either of the two cannot be read", () => {
    expect(diskMegabytes(null, 2 * MB)).toBeNull();
    expect(diskMegabytes(2 * MB, null)).toBeNull();
    expect(diskMegabytes(null, null)).toBeNull();
  });
});

describe("the machine's tags", () => {
  it("names the system and how long it has been up", () => {
    expect(machineTags(stats().machine)).toEqual([
      { text: "Windows 11 Pro" },
      { text: "up 3 days" },
    ]);
  });

  it.each([
    ["no system name", { os: "" }, [{ text: "up 3 days" }]],
    ["no uptime", { uptime_seconds: null }, [{ text: "Windows 11 Pro" }]],
    ["nothing to say", { os: "", uptime_seconds: null }, []],
  ])("leaves out what it cannot say: %s", (_name, change, tags) => {
    expect(machineTags({ ...stats().machine, ...change })).toEqual(tags);
  });

  it("warns when a reboot is waiting", () => {
    const tags = machineTags({ ...stats().machine, reboot_pending: true });
    expect(tags.at(-1)).toEqual({
      text: "reboot pending",
      meaning: "attention",
    });
  });
});
