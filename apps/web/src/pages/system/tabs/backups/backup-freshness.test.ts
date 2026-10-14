import { describe, expect, it } from "vitest";

import type { AppSettings } from "../../../../lib/settings/types";
import { backupFreshness } from "./backup-freshness";

const HOUR_MS = 60 * 60 * 1000;
const NOW = Date.parse("2026-10-02T12:00:00Z");

function settings(overrides: Partial<AppSettings>): AppSettings {
  return {
    setup_wizard_state: "completed",
    app_timezone: "UTC",
    log_retention_days: 30,
    activity_retention_days: 90,
    configuration_backup_enabled: true,
    configuration_backup_interval_hours: 6,
    configuration_backup_preferred_time: "02:00",
    configuration_backup_last_run_at: null,
    updated_at: "2026-10-01T00:00:00Z",
    ...overrides,
  };
}

describe("backupFreshness", () => {
  it("says nothing when automatic backups are off", () => {
    expect(
      backupFreshness(settings({ configuration_backup_enabled: false }), NOW),
    ).toBeNull();
  });

  it("is up to date while the next backup is not yet due", () => {
    const lastRun = new Date(NOW - 2 * HOUR_MS).toISOString();

    expect(
      backupFreshness(
        settings({ configuration_backup_last_run_at: lastRun }),
        NOW,
      ),
    ).toBe("up_to_date");
  });

  it("is overdue once the next backup is more than an hour late", () => {
    const lastRun = new Date(NOW - 10 * HOUR_MS).toISOString();

    expect(
      backupFreshness(
        settings({ configuration_backup_last_run_at: lastRun }),
        NOW,
      ),
    ).toBe("overdue");
  });
});
