import { expect, it } from "vitest";

import { backupFreshness } from "./backup-freshness";

const HOUR_MS = 60 * 60 * 1000;
const NOW = Date.parse("2026-10-02T12:00:00Z");

it("reads the schedule in the browser's own zone when no time zone is saved", () => {
  const settings = {
    setup_wizard_state: "completed",
    app_timezone: "",
    log_retention_days: 30,
    activity_retention_days: 90,
    configuration_backup_enabled: true,
    configuration_backup_interval_hours: 6,
    configuration_backup_preferred_time: "02:00",
    configuration_backup_last_run_at: new Date(NOW - 2 * HOUR_MS).toISOString(),
    updated_at: "2026-10-01T00:00:00Z",
  };

  expect(backupFreshness(settings, NOW)).toBe("up_to_date");
});
