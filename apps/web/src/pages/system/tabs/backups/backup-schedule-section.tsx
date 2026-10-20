import { Chip } from "../../../../components/panels/chip";
import { Panel } from "../../../../components/panels/panel";
import { quietActionRowClass } from "../../../../components/shared/quiet-section";
import { SettingRow } from "../../../../components/shared/settings-group";
import { MmOnOffSwitch } from "../../../../components/ui/mm-on-off-switch";
import { errorMessage } from "../../../../lib/api/error-message";
import type {
  AppSettings,
  ConfigurationBackupItem,
} from "../../../../lib/settings/types";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { useAppDateFormatter } from "../../../../lib/ui/mm-format-date";
import { useNow } from "../../../../lib/ui/use-now";
import {
  DEFAULT_BACKUP_TIME,
  type SystemSettingsForm,
} from "../../use-system-settings-form";
import { BACKUP_FRESHNESS_MEANING, backupFreshness } from "./backup-freshness";
import { madeByWords, newestBackup } from "./last-backup";

const BACKUP_INTERVAL_HOURS = [6, 12, 24, 48, 72, 168] as const;
const HOURS_PER_WEEK = 168;
/** How often "overdue" is worked out again while the page is open. */
const CLOCK_TICK_MS = 60_000;

const WHAT_A_BACKUP_HOLDS =
  "A backup holds your workflows, rules, schedule and time zone, plus your media managers, download clients and alerts. Their API keys, passwords and alert addresses are in it only when a backup password is set, locked with that password; without one, enter them again after restoring. It does not include your sign-in or file history.";

/**
 * When Weir backs up its own settings. The choices are drafts until Save, which appears once one has changed: they
 * share one save with how long things are kept, so nothing here is applied on its own.
 */
export function BackupScheduleSection({
  form,
  settings,
  backups,
  passwordSet,
  onSave,
  saved,
}: {
  form: SystemSettingsForm;
  settings: AppSettings;
  /** The backups on this machine, or undefined while they are still being read. */
  backups: readonly ConfigurationBackupItem[] | undefined;
  /** Whether a backup password is set, so the note says whether keys and passwords go into a backup. */
  passwordSet: boolean;
  onSave: () => void;
  saved: boolean;
}) {
  const formatDate = useAppDateFormatter();
  const now = useNow(CLOCK_TICK_MS);
  const { backupSchedule: schedule, save } = form;
  const freshness = backupFreshness(settings, now);
  const last = backups ? newestBackup(backups) : null;
  const lastMadeBy = last ? madeByWords(last.made_by) : "";
  const failed = save.isError && form.lastSaveTarget === "backup";

  return (
    <Panel
      title="Schedule"
      headingId="suite-settings-backup-heading"
      padded
      note={
        <span title={WHAT_A_BACKUP_HOLDS}>
          {passwordSet
            ? "Workflows, rules and settings, with your keys and passwords locked by the backup password."
            : "Workflows, rules and settings. Keys and passwords aren't included until you set a backup password."}
        </span>
      }
    >
      <div className="mm-setgroup__rows">
        <SettingRow label="Back up automatically">
          <span className="mm-sys-switch">
            <MmOnOffSwitch
              id="backup-scheduled"
              label="Back up automatically"
              layout="control"
              enabled={schedule.enabled}
              disabled={save.isPending}
              onChange={schedule.setEnabled}
            />
          </span>
        </SettingRow>
        <SettingRow label="Every" htmlFor="backup-interval">
          <select
            id="backup-interval"
            className="mm-input mm-sys-field"
            value={schedule.intervalHours}
            disabled={save.isPending}
            onChange={(e) => schedule.setIntervalHours(Number(e.target.value))}
          >
            {BACKUP_INTERVAL_HOURS.map((h) => (
              <option key={h} value={h}>
                {h === HOURS_PER_WEEK ? "7 days" : `${h} hours`}
              </option>
            ))}
          </select>
        </SettingRow>
        <SettingRow label="At" htmlFor="backup-time">
          <input
            id="backup-time"
            type="time"
            className="mm-input mm-sys-field"
            value={schedule.time}
            disabled={save.isPending}
            onChange={(e) =>
              schedule.setTime(e.target.value || DEFAULT_BACKUP_TIME)
            }
          />
        </SettingRow>
        <SettingRow label="Last backup">
          <span className="mm-sys-value" data-testid="backup-last">
            {backups && !last ? "None yet" : formatDate(last?.created_at)}
            {lastMadeBy ? (
              <span className="mm-sys-value__how">({lastMadeBy})</span>
            ) : null}
            {freshness ? (
              <Chip meaning={BACKUP_FRESHNESS_MEANING[freshness]}>
                {freshness === "overdue" ? "Overdue" : "Up to date"}
              </Chip>
            ) : null}
          </span>
        </SettingRow>
      </div>
      {schedule.dirty || failed || saved ? (
        <div className={quietActionRowClass}>
          {schedule.dirty ? (
            <button
              type="button"
              className={`${mmActionButtonClass({ variant: "primary" })} mm-sys-btn`}
              disabled={save.isPending}
              onClick={onSave}
            >
              {save.isPending ? "Saving…" : "Save schedule"}
            </button>
          ) : null}
          {failed ? (
            <p
              className="mm-status-text text-sm"
              data-status="broken"
              role="alert"
              data-testid="suite-settings-backup-save-error"
            >
              {errorMessage(save.error, "Could not save.")}
            </p>
          ) : saved && !schedule.dirty ? (
            <p
              className="mm-status-text text-sm"
              data-status="done"
              role="status"
            >
              Backup schedule saved.
            </p>
          ) : null}
        </div>
      ) : null}
    </Panel>
  );
}
