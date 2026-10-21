import { describe, expect, it } from "vitest";

import type { SystemOverview } from "../../../../lib/system/system-stats-types";
import { newestBackup, ringFigures, weirFacts } from "./this-weir-model";

const NOW = Date.parse("2026-10-02T12:00:00Z");

const overview: SystemOverview = {
  version: "3.2.16",
  update: { status: "up_to_date", latest_version: "3.2.16" },
  uptime_seconds: 100,
  started_at: "2026-10-02T11:00:00Z",
  runs_as: "service",
  address: "http://192.168.1.5:9347/",
  data_bytes: 5 * 1024 * 1024,
  browsers_live: 2,
  requests: { median_ms: 6.4, p95_ms: 20, errors_today: 0 },
  jobs_today: { run: 1200, failed: 2 },
  restarts_this_week: 0,
  checks: { passing: 9, total: 10 },
  last_update_backup: null,
};

const facts = (changes: Partial<Parameters<typeof weirFacts>[0]> = {}) =>
  Object.fromEntries(
    weirFacts({
      overview,
      work: { running: 2, slots: 4 },
      usage: { cpuPercent: 3.4, memoryBytes: 265 * 1024 * 1024 },
      waiting: 3,
      lastBackupAt: "2026-10-02T09:00:00Z",
      lastBackupBytes: 2048,
      now: NOW,
      ...changes,
    }).map((fact) => [fact.key, fact]),
  );

describe("the health ring", () => {
  it("fills by the share of checks that pass and counts those that need a person", () => {
    expect(
      ringFigures({ passing: 9, total: 10, need: 1, meaning: "attention" }),
    ).toEqual({
      fraction: 0.9,
      passing: 9,
      total: 10,
      needYou: 1,
      meaning: "attention",
    });
  });

  it("is full when there are no checks to fail", () => {
    expect(
      ringFigures({ passing: 0, total: 0, need: 0, meaning: "done" }).fraction,
    ).toBe(1);
  });

  it("never counts more passing than there are checks", () => {
    expect(
      ringFigures({ passing: 12, total: 10, need: 0, meaning: "done" }).passing,
    ).toBe(10);
  });

  it("does not call a check that is only unproven a need", () => {
    expect(
      ringFigures({ passing: 8, total: 10, need: 0, meaning: "done" }).needYou,
    ).toBe(0);
  });
});

describe("the fact tiles", () => {
  it("shows the version with where the update stands", () => {
    expect(facts().version).toMatchObject({
      value: "3.2.16",
      sub: "up to date",
      meaning: "done",
    });
    expect(
      facts({
        overview: {
          ...overview,
          update: { status: "update_available", latest_version: "3.3.0" },
        },
      }).version.sub,
    ).toBe("3.3.0 ready");
  });

  it("counts the uptime from when Weir started, so it ticks with the clock", () => {
    expect(facts().uptime.value).toBe("1h 00m");
    expect(facts({ now: NOW + 60_000 }).uptime.value).toBe("1h 01m");
  });

  it("asks for attention on the uptime's note when Weir restarted this week", () => {
    const restarted = facts({
      overview: { ...overview, restarts_this_week: 3 },
    });
    expect(restarted.uptime).toMatchObject({
      sub: "3 restarts",
      meaning: "attention",
    });
  });

  it("shows the files at once as running over slots", () => {
    expect(facts()["files-at-once"]).toMatchObject({
      value: "2 / 4",
      sub: "running",
    });
    expect(facts({ work: { running: 0, slots: 4 } })["files-at-once"].sub).toBe(
      "idle",
    );
    expect(facts({ work: null })["files-at-once"].value).toBe("–");
  });

  it("shows jobs run and failed today", () => {
    expect(facts().jobs).toMatchObject({
      value: "1,200 run",
      sub: "2 failed",
      meaning: "broken",
    });
    expect(
      facts({ overview: { ...overview, jobs_today: { run: 5, failed: 0 } } })
        .jobs,
    ).toMatchObject({ sub: "none failed", meaning: "done" });
  });

  it("shows the median response and what 95% of responses beat", () => {
    expect(facts().response).toMatchObject({
      label: "Response",
      value: "6 ms",
      sub: "p95 20 ms",
    });
  });

  it("shows when the last backup was made, or that there is none", () => {
    expect(facts().backup).toMatchObject({ value: "3 h ago", sub: "2.0 KB" });
    expect(
      facts({ lastBackupAt: null, lastBackupBytes: null }).backup,
    ).toMatchObject({
      value: "None yet",
      sub: "none made",
    });
  });

  it("says in words how Weir runs", () => {
    expect(facts()["runs-as"]).toMatchObject({ value: "Service" });
    expect(
      facts({ overview: { ...overview, runs_as: "docker" } })["runs-as"].value,
    ).toBe("Docker");
  });

  it("splits the address into host and port", () => {
    expect(facts().address).toMatchObject({
      value: "192.168.1.5",
      sub: "port 9347",
    });
  });

  it("shows the size of Weir's data and how many browsers are open", () => {
    expect(facts().data.value).toBe("5.0 MB");
    expect(facts().browsers.value).toBe("2");
  });
});

describe("Weir's own use and its queue", () => {
  it("shows Weir's share of the processor and its memory", () => {
    expect(facts().usage).toMatchObject({ value: "3% CPU", sub: "265 MB RAM" });
  });

  it("shows a dash where a reading is missing, never a made-up zero", () => {
    expect(
      facts({ usage: { cpuPercent: null, memoryBytes: null } }).usage,
    ).toMatchObject({ value: "–", sub: "" });
    expect(facts({ waiting: null }).waiting).toMatchObject({
      value: "–",
      sub: "",
    });
  });

  it("says how many files wait for a free slot, or that nothing is queued", () => {
    expect(facts().waiting).toMatchObject({
      value: "3",
      sub: "for a free slot",
    });
    expect(facts({ waiting: 0 }).waiting).toMatchObject({
      value: "0",
      sub: "nothing queued",
    });
  });
});

describe("the order of the tiles", () => {
  it("puts what matters most first, so a short card shows the version, uptime, files at once and jobs", () => {
    const keys = weirFacts({
      overview,
      work: null,
      usage: null,
      waiting: null,
      lastBackupAt: null,
      lastBackupBytes: null,
      now: NOW,
    }).map((fact) => fact.key);

    expect(keys).toEqual([
      "version",
      "uptime",
      "files-at-once",
      "jobs",
      "usage",
      "waiting",
      "backup",
      "response",
      "address",
      "runs-as",
      "data",
      "browsers",
    ]);
  });

  it("offers a computer's first name where its whole name may not fit, but never for an address of numbers", () => {
    expect(
      facts({
        overview: { ...overview, address: "http://media-pc.home.lan:9347/" },
      }).address,
    ).toMatchObject({ value: "media-pc.home.lan", valueShort: "media-pc" });
    expect(facts().address.valueShort).toBeUndefined();
  });
});

describe("the fact tiles when a reading is odd", () => {
  it("says nothing of the size of a backup whose size is unknown", () => {
    expect(facts({ lastBackupBytes: null }).backup).toMatchObject({
      value: "3 h ago",
      sub: "",
    });
  });

  it("falls back on the uptime the server reported when it gave no readable start time", () => {
    expect(
      facts({ overview: { ...overview, started_at: "not a time" } }).uptime
        .value,
    ).toBe("1:40");
  });

  it.each([
    ["no host at all", "", "–", "", undefined],
    ["a host with no port", "https://weir.home", "weir.home", "", "weir"],
    [
      "a bracketed address",
      "http://[::1]:9347/",
      "[::1]",
      "port 9347",
      undefined,
    ],
    ["a host with no dot", "http://nas:9347/", "nas", "port 9347", undefined],
  ])("shows the address for %s", (_name, address, value, sub, valueShort) => {
    expect(facts({ overview: { ...overview, address } }).address).toMatchObject(
      { value, sub, valueShort },
    );
  });
});

describe("the newest backup", () => {
  it("is the one made last, whatever order the list is in", () => {
    const older = { created_at: "2026-10-01T03:00:00", size_bytes: 1 };
    const newer = { created_at: "2026-10-02T03:00:00", size_bytes: 2 };
    expect(newestBackup([newer, older])).toBe(newer);
    expect(newestBackup([older, newer])).toBe(newer);
  });

  it("keeps the first of two made at the same time, and reads a backup with no readable time as the oldest", () => {
    const first = { created_at: "2026-10-02T03:00:00", size_bytes: 1 };
    const same = { created_at: "2026-10-02T03:00:00", size_bytes: 2 };
    const unreadable = { created_at: "not a time", size_bytes: 3 };
    expect(newestBackup([first, same])).toBe(first);
    expect(newestBackup([unreadable, first])).toBe(first);
    expect(newestBackup([first, unreadable])).toBe(first);
  });

  it("is nothing when there are no backups", () => {
    expect(newestBackup([])).toBeNull();
  });
});
