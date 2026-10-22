import { describe, expect, it } from "vitest";

import type { SystemDrive } from "../../../../lib/system/system-stats-types";
import { driveBlocks, storageCount } from "./storage-model";

const GB = 1024 ** 3;
const MB = 1024 ** 2;

const drive = (changes: Partial<SystemDrive> = {}): SystemDrive => ({
  name: "D:",
  path: "D:\\",
  total_bytes: 1000 * GB,
  free_bytes: 400 * GB,
  weir_bytes: 100 * GB,
  keep_free_bytes: 20 * GB,
  full_in_days: null,
  read_bytes_per_sec: 4 * MB,
  write_bytes_per_sec: 12 * MB,
  busy_percent: 18,
  workflows: [
    { id: 1, name: "Movies", roles: ["watched", "work", "output"] },
    { id: 2, name: "TV", roles: ["output"] },
  ],
  ...changes,
});

describe("a drive's block", () => {
  it("splits the bar into Weir's work files and everything else", () => {
    const block = driveBlocks([drive()])[0];
    expect(block.weirShare).toBeCloseTo(10);
    expect(block.otherShare).toBeCloseTo(50);
  });

  it("puts the keep-free line where the room Weir keeps free begins", () => {
    const block = driveBlocks([drive()])[0];
    expect(block.keepFreeAt).toBeCloseTo(98);
    expect(block.keepFreeLine).toBe("keeps 20.00 GB free");
  });

  it("has no keep-free line where no workflow keeps room free", () => {
    const block = driveBlocks([drive({ keep_free_bytes: 0 })])[0];
    expect(block.keepFreeAt).toBeNull();
    expect(block.keepFreeLine).toBe("");
  });

  it("is low when there is less room than the keep-free setting", () => {
    expect(driveBlocks([drive({ free_bytes: 10 * GB })])[0].low).toBe(true);
    expect(driveBlocks([drive()])[0].low).toBe(false);
  });

  it("says how much is free and when the drive will be full", () => {
    const blocks = driveBlocks([
      drive({ full_in_days: 12.2 }),
      drive({ name: "E:", path: "E:\\" }),
    ]);
    expect(blocks[0].freeLine).toBe("400 GB free · full in ~12 days");
    expect(blocks[1].freeLine).toBe("400 GB free");
  });

  it("reads the drive's speed and busy time, and has none to read on a network share", () => {
    expect(driveBlocks([drive()])[0].ioLine).toBe(
      "read 4.0 · write 12 MB/s · 18% busy",
    );
    expect(
      driveBlocks([
        drive({
          read_bytes_per_sec: null,
          write_bytes_per_sec: null,
          busy_percent: null,
        }),
      ])[0].ioLine,
    ).toBe("");
  });

  it("gives the speed without a busy time when the drive does not report one", () => {
    expect(driveBlocks([drive({ busy_percent: null })])[0].ioLine).toBe(
      "read 4.0 · write 12 MB/s",
    );
  });

  it("names the workflows that use it", () => {
    expect(driveBlocks([drive()])[0].workflows).toBe("Movies, TV");
  });

  it("keeps the bar inside the drive for a drive of no size", () => {
    const block = driveBlocks([
      drive({ total_bytes: 0, free_bytes: 0, weir_bytes: 0 }),
    ])[0];
    expect(block).toMatchObject({
      weirShare: 0,
      otherShare: 0,
      keepFreeAt: null,
    });
  });
});

describe("the card's count", () => {
  it("adds up the room across the drives", () => {
    expect(storageCount([drive(), drive({ name: "E:" })])).toBe("800 GB free");
  });

  it("is empty with no drives", () => {
    expect(storageCount([])).toBe("");
  });
});
