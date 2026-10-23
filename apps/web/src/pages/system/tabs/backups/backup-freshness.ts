import type { AppSettings } from "../../../../lib/settings/types";
import { parseAppTime } from "../../../../lib/ui/mm-format-date";
import type { StatusMeaning } from "../../../../lib/ui/status-meaning";
import { nextBackupAt } from "../../../processing/dashboard/system/backup-schedule";

/** How long past its time an automatic backup may be before it is called overdue: the server looks once a minute. */
const OVERDUE_AFTER_MS = 60 * 60 * 1000;

export type BackupFreshness = "up_to_date" | "overdue";

export const BACKUP_FRESHNESS_MEANING: Record<BackupFreshness, StatusMeaning> =
  {
    up_to_date: "done",
    overdue: "attention",
  };

/**
 * Whether the automatic backup is keeping to its schedule, by the saved schedule and the last backup it made. Null when
 * automatic backups are off, so there is nothing to keep to.
 */
export function backupFreshness(
  settings: AppSettings,
  now: number,
): BackupFreshness | null {
  const next = nextBackupAt(
    {
      enabled: Boolean(settings.configuration_backup_enabled),
      intervalHours: Number(settings.configuration_backup_interval_hours),
      preferredTime: settings.configuration_backup_preferred_time,
      lastRunAt: parseAppTime(settings.configuration_backup_last_run_at),
    },
    now,
    settings.app_timezone || undefined,
  );
  if (next === null) return null;
  return next < now - OVERDUE_AFTER_MS ? "overdue" : "up_to_date";
}
