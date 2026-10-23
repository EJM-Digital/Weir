import { describe, expect, it } from "vitest";

import { nextBackupAt, type BackupRun } from "./backup-schedule";

const at = (iso: string) => Date.parse(iso);

function run(overrides: Partial<BackupRun> = {}): BackupRun {
  return {
    enabled: true,
    intervalHours: 24,
    preferredTime: "03:00",
    lastRunAt: at("2026-10-01T03:00:00Z"),
    ...overrides,
  };
}

describe("nextBackupAt", () => {
  it("is null when automatic backups are off", () => {
    expect(
      nextBackupAt(run({ enabled: false }), at("2026-10-02T01:00:00Z"), "UTC"),
    ).toBeNull();
  });

  it("is the last backup plus the interval when the interval is under a day", () => {
    expect(
      nextBackupAt(
        run({ intervalHours: 6, lastRunAt: at("2026-10-02T03:00:00Z") }),
        at("2026-10-02T04:00:00Z"),
        "UTC",
      ),
    ).toBe(at("2026-10-02T09:00:00Z"));
  });

  it("waits for the preferred time on the next day for a daily backup", () => {
    expect(nextBackupAt(run(), at("2026-10-02T01:00:00Z"), "UTC")).toBe(
      at("2026-10-02T03:00:00Z"),
    );
  });

  it("is due now when the interval has passed, it is a new day and the preferred time has gone", () => {
    const next = nextBackupAt(run(), at("2026-10-02T10:00:00Z"), "UTC");
    expect(next).not.toBeNull();
    expect(next as number).toBeLessThanOrEqual(at("2026-10-02T10:00:00Z"));
  });

  it("goes to the next day once today's backup has run", () => {
    expect(
      nextBackupAt(
        run({ lastRunAt: at("2026-10-02T03:00:00Z") }),
        at("2026-10-02T10:00:00Z"),
        "UTC",
      ),
    ).toBe(at("2026-10-03T03:00:00Z"));
  });

  it("is the preferred time today for a backup that has never run, or now when that has gone", () => {
    expect(
      nextBackupAt(run({ lastRunAt: null }), at("2026-10-02T01:00:00Z"), "UTC"),
    ).toBe(at("2026-10-02T03:00:00Z"));
    expect(
      nextBackupAt(run({ lastRunAt: null }), at("2026-10-02T10:00:00Z"), "UTC"),
    ).toBe(at("2026-10-02T10:00:00Z"));
  });

  it("takes the preferred time and the day in the timezone chosen", () => {
    // 03:00 in Sydney on 2 October (UTC+10) is 17:00 UTC on the 1st.
    expect(
      nextBackupAt(
        run({ lastRunAt: at("2026-09-30T17:00:00Z") }),
        at("2026-10-01T10:00:00Z"),
        "Australia/Sydney",
      ),
    ).toBe(at("2026-10-01T17:00:00Z"));
  });

  it("holds to the interval across the clocks going forward", () => {
    // Sydney's clocks go forward at 2am on 4 October, so a day after 03:00 on the 3rd is 04:00 local.
    expect(
      nextBackupAt(
        run({ lastRunAt: at("2026-10-02T17:00:00Z") }),
        at("2026-10-03T10:00:00Z"),
        "Australia/Sydney",
      ),
    ).toBe(at("2026-10-03T17:00:00Z"));
  });

  it("does not run twice on the day the clocks go back, when a day after the last backup is still that day", () => {
    // New York's clocks go back at 2am on 1 November: a backup at 00:30 that day is followed, 24 hours on, by 23:30 the same day.
    expect(
      nextBackupAt(
        run({ lastRunAt: at("2026-11-01T04:30:00Z") }),
        at("2026-11-01T05:00:00Z"),
        "America/New_York",
      ),
    ).toBe(at("2026-11-02T08:00:00Z"));
  });

  it("falls back to 02:00 for a preferred time it cannot read", () => {
    expect(
      nextBackupAt(
        run({ preferredTime: "soon", lastRunAt: null }),
        at("2026-10-02T01:00:00Z"),
        "UTC",
      ),
    ).toBe(at("2026-10-02T02:00:00Z"));
  });

  it("uses the browser timezone for a name it does not know, rather than failing", () => {
    expect(
      nextBackupAt(run(), at("2026-10-02T01:00:00Z"), "Not/AZone"),
    ).not.toBeNull();
  });
});
