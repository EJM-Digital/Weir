import { describe, expect, it } from "vitest";

import {
  latestBackups,
  nextBackupWords,
  scheduleWords,
  timeOfDayWords,
  updateFacts,
} from "./backups-card-model";

const NOW = Date.parse("2026-10-02T10:00:00Z");

describe("latestBackups", () => {
  it("lists the newest three, newest first, with their sizes", () => {
    const rows = latestBackups([
      {
        id: 1,
        created_at: "2026-09-29T03:00:00Z",
        file_name: "a",
        size_bytes: 1024,
        made_by: "schedule",
      },
      {
        id: 4,
        created_at: "2026-10-02T03:00:00Z",
        file_name: "d",
        size_bytes: 3 * 1024 * 1024,
        made_by: "schedule",
      },
      {
        id: 2,
        created_at: "2026-09-30T03:00:00Z",
        file_name: "b",
        size_bytes: 2048,
        made_by: "schedule",
      },
      {
        id: 3,
        created_at: "2026-10-01T03:00:00Z",
        file_name: "c",
        size_bytes: 4096,
        made_by: "schedule",
      },
    ]);
    expect(rows.map((row) => row.id)).toEqual([4, 3, 2]);
    expect(rows[0].size).toBe("3.0 MB");
  });

  it("leaves out a backup whose time cannot be read", () => {
    expect(
      latestBackups([
        {
          id: 1,
          created_at: "never",
          file_name: "a",
          size_bytes: 1,
          made_by: "schedule",
        },
      ]),
    ).toEqual([]);
  });
});

describe("scheduleWords", () => {
  it("says how often and when", () => {
    expect(
      scheduleWords({
        enabled: true,
        intervalHours: 24,
        preferredTime: "03:00",
      }),
    ).toMatch(/^Every day at 3:00\s?am$/);
  });

  it.each([
    [48, "Every 2 days"],
    [12, "Every 12 hours"],
    [168, "Every 7 days"],
  ])("writes %i hours as %s", (hours, words) => {
    expect(
      scheduleWords({
        enabled: true,
        intervalHours: hours,
        preferredTime: "03:00",
      }),
    ).toContain(words);
  });

  it("says automatic backups are off when they are", () => {
    expect(
      scheduleWords({
        enabled: false,
        intervalHours: 24,
        preferredTime: "03:00",
      }),
    ).toBe("Automatic backups are off");
  });
});

describe("timeOfDayWords", () => {
  it("shows a time that does not read as it is", () => {
    expect(timeOfDayWords("soon")).toBe("soon");
  });
});

describe("nextBackupWords", () => {
  it("counts to the next backup", () => {
    expect(nextBackupWords(NOW + 5 * 3_600_000, NOW)).toBe("next in 5 h");
  });

  it("says a backup whose time has come is due now", () => {
    expect(nextBackupWords(NOW - 1000, NOW)).toBe("due now");
  });

  it("says nothing when there is no next backup", () => {
    expect(nextBackupWords(null, NOW)).toBe("");
  });
});

describe("updateFacts", () => {
  const status = {
    current_version: "3.2.16",
    latest_version: "3.3.0",
    install_type: "windows",
    in_app_upgrade_supported: true,
    summary: "",
  };

  it("says an update is available", () => {
    expect(
      updateFacts({ ...status, status: "update_available" }, undefined),
    ).toMatchObject({
      state: "Update available",
      meaning: "todo",
      latest: "3.3.0",
    });
  });

  it("says when the update has been downloaded", () => {
    expect(
      updateFacts(
        { ...status, status: "update_available" },
        {
          downloaded: true,
          pending_version: "3.3.0",
          state: "downloaded",
          failure: null,
          tray_running: true,
        },
      ),
    ).toMatchObject({ state: "Downloaded and ready", meaning: "todo" });
  });

  it("says Weir is up to date", () => {
    expect(
      updateFacts(
        { ...status, status: "up_to_date", latest_version: "3.2.16" },
        undefined,
      ),
    ).toMatchObject({ state: "Up to date", meaning: "done" });
  });

  it("says Weir could not check, with no latest version", () => {
    expect(
      updateFacts(
        { ...status, status: "unavailable", latest_version: null },
        undefined,
      ),
    ).toMatchObject({
      state: "Could not check",
      latest: "—",
      meaning: "attention",
    });
  });
});
