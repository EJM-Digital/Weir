import { useCallback, useEffect, useState } from "react";

import { useAppSettingsSaveMutation } from "../../lib/settings/queries";
import type { AppSettings, AppSettingsPutBody } from "../../lib/settings/types";

const MAX_RETENTION_DAYS = 3650;
/** What an emptied System log retention field saves as. */
const DEFAULT_LOG_RETENTION_DAYS = 30;
const DEFAULT_BACKUP_INTERVAL_HOURS = 24;
const MAX_BACKUP_INTERVAL_HOURS = 720;
export const DEFAULT_BACKUP_TIME = "02:00";

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(Math.trunc(value), min), max);
}

function savedBackupTime(settings: AppSettings): string {
  return settings.configuration_backup_preferred_time.trim();
}

/** How long the System log and Events are kept, as the Logs tab edits them. */
function useRetentionDraft(settings: AppSettings | undefined) {
  const [logDraft, setLogDraft] = useState<string | null>(null);
  const [activityDraft, setActivityDraft] = useState<string | null>(null);
  const logValue = logDraft ?? String(settings?.log_retention_days ?? "");
  const activityValue =
    activityDraft ?? String(settings?.activity_retention_days ?? "");
  const discard = useCallback(() => {
    setLogDraft(null);
    setActivityDraft(null);
  }, []);

  return {
    logValue,
    activityValue,
    setLogDraft,
    setActivityDraft,
    discard,
    /** Blank saves the default; nonsense keeps the saved value; anything else is clamped to 1..3650. */
    finalizeLogDays: (): number => {
      const raw = logValue.trim();
      if (raw === "") return DEFAULT_LOG_RETENTION_DAYS;
      const n = Number(raw);
      if (!Number.isFinite(n)) {
        return settings?.log_retention_days ?? DEFAULT_LOG_RETENTION_DAYS;
      }
      return clamp(n, 1, MAX_RETENTION_DAYS);
    },
    /** 0 keeps Events until they are cleared; blank or nonsense keeps the saved value. */
    finalizeActivityDays: (): number => {
      const raw = activityValue.trim();
      const n = Number(raw);
      if (raw === "" || !Number.isFinite(n)) {
        return settings?.activity_retention_days ?? 0;
      }
      return clamp(n, 0, MAX_RETENTION_DAYS);
    },
    dirty:
      settings !== undefined &&
      ((logDraft !== null &&
        logDraft !== String(settings.log_retention_days)) ||
        (activityDraft !== null &&
          activityDraft !== String(settings.activity_retention_days))),
  };
}

/** When Weir backs up its own settings, as the Backups tab edits it. */
function useBackupScheduleDraft(settings: AppSettings | undefined) {
  const [enabled, setEnabled] = useState(false);
  const [intervalHours, setIntervalHours] = useState(
    DEFAULT_BACKUP_INTERVAL_HOURS,
  );
  const [time, setTime] = useState(DEFAULT_BACKUP_TIME);
  const discard = useCallback((saved: AppSettings) => {
    setEnabled(Boolean(saved.configuration_backup_enabled));
    setIntervalHours(saved.configuration_backup_interval_hours);
    setTime(savedBackupTime(saved));
  }, []);

  return {
    enabled,
    setEnabled,
    intervalHours,
    setIntervalHours,
    time,
    setTime,
    discard,
    dirty:
      settings !== undefined &&
      (enabled !== Boolean(settings.configuration_backup_enabled) ||
        intervalHours !== settings.configuration_backup_interval_hours ||
        time !== savedBackupTime(settings)),
  };
}

export type SaveTarget = "logs" | "backup";

/**
 * The two System forms that write Weir's settings: how long things are kept (Logs) and the backup
 * schedule (Backups). They share one PUT, so a save sends both, and one unsaved-changes guard; the
 * drafts live above the tabs, so switching tabs keeps them.
 */
export function useSystemSettingsForm(settings: AppSettings | undefined) {
  const save = useAppSettingsSaveMutation();
  const retention = useRetentionDraft(settings);
  const backupSchedule = useBackupScheduleDraft(settings);
  const [lastSaveTarget, setLastSaveTarget] = useState<SaveTarget | null>(null);
  const { discard: discardRetention } = retention;
  const { discard: discardBackup } = backupSchedule;

  // A fresh read from the server replaces every draft with what was saved.
  useEffect(() => {
    if (!settings) return;
    discardRetention();
    discardBackup(settings);
  }, [settings, discardRetention, discardBackup]);

  const putBody = (current: AppSettings): AppSettingsPutBody => {
    return {
      setup_wizard_state: current.setup_wizard_state,
      app_timezone: current.app_timezone,
      log_retention_days: retention.finalizeLogDays(),
      activity_retention_days: retention.finalizeActivityDays(),
      configuration_backup_enabled: backupSchedule.enabled,
      configuration_backup_interval_hours: clamp(
        backupSchedule.intervalHours,
        1,
        MAX_BACKUP_INTERVAL_HOURS,
      ),
      configuration_backup_preferred_time: backupSchedule.time,
    };
  };

  /** Saves both forms; `target` decides which one shows a failure. */
  const saveFrom = (
    target: SaveTarget,
    callbacks: {
      onSaved?: () => void;
      onFailed?: (error: unknown) => void;
    } = {},
  ) => {
    if (!settings) return;
    setLastSaveTarget(target);
    save.reset();
    save.mutate(putBody(settings), {
      onSuccess: () => {
        setLastSaveTarget(null);
        callbacks.onSaved?.();
      },
      onError: (error) => callbacks.onFailed?.(error),
    });
  };

  return {
    save,
    lastSaveTarget,
    saveFrom,
    isDirty: retention.dirty || backupSchedule.dirty,
    retention,
    backupSchedule,
  };
}

export type SystemSettingsForm = ReturnType<typeof useSystemSettingsForm>;
