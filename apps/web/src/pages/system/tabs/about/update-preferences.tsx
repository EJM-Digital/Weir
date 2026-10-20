import { useState } from "react";

import {
  SegmentedControl,
  type SegmentedOption,
} from "../../../../components/panels/segmented-control";
import { LoadError } from "../../../../components/shared/load-error";
import { SettingRow } from "../../../../components/shared/settings-group";
import { MmOnOffSwitch } from "../../../../components/ui/mm-on-off-switch";
import { errorMessage } from "../../../../lib/api/error-message";
import {
  useUpdateSettingsMutation,
  useUpdateSettingsQuery,
} from "../../../../lib/settings/queries";
import type {
  UpdateMode,
  UpdateSettingsOut,
} from "../../../../lib/settings/types";

const UPDATE_MODES: readonly {
  value: UpdateMode;
  label: string;
  /** One line under the choice. */
  summary: string;
  /** What the mode does in full, for the hover note. */
  detail: string;
}[] = [
  {
    value: "Auto",
    label: "Auto",
    summary: "Installs once Weir has been idle 5 minutes.",
    detail:
      "Downloads updates automatically. Once one is downloaded, Weir installs it and restarts by itself when it has been idle for 5 minutes, with no file being processed, handed back or waiting to start. It also installs when Weir next quits or starts, or when you choose Restart and apply.",
  },
  {
    value: "DownloadOnly",
    label: "Download only",
    summary: "Installs when you restart Weir.",
    detail:
      "Downloads updates silently in the background, then notifies you when ready to install. Weir never restarts by itself to install one; it installs when you choose Restart and apply, or when Weir next quits or starts.",
  },
  {
    value: "NotifyOnly",
    label: "Notify only",
    summary: "Tells you and downloads nothing.",
    detail:
      "Alerts you when an update is available without downloading anything.",
  },
];

const MODE_OPTIONS: readonly SegmentedOption<UpdateMode>[] = UPDATE_MODES.map(
  ({ value, label }) => ({ value, label }),
);

const CHECK_INTERVALS = [
  { value: 15, label: "15 minutes" },
  { value: 30, label: "30 minutes" },
  { value: 60, label: "1 hour" },
  { value: 120, label: "2 hours" },
  { value: 360, label: "6 hours" },
  { value: 720, label: "12 hours" },
  { value: 1440, label: "24 hours" },
];

const CHECK_FOR_UPDATES_ID = "update-check-for-updates";
const CHECK_ON_STARTUP_ID = "update-check-on-startup";
const CHECK_INTERVAL_ID = "update-check-interval";

/**
 * How the Windows tray app handles updates. Every choice saves as it is made: there is nothing to confirm,
 * and what is shown is what the tray will do.
 */
export function UpdatePreferences() {
  const settingsQ = useUpdateSettingsQuery(true);
  const save = useUpdateSettingsMutation();
  const [saved, setSaved] = useState(false);
  const server = settingsQ.data;
  // While a save is in flight its values are the ones shown, so two quick changes do not undo each other.
  const current = server && {
    ...server,
    ...(save.isPending ? save.variables : undefined),
  };

  if (settingsQ.isPending) {
    return <p className="mm-quiet-note">Loading update preferences...</p>;
  }
  if (!current) {
    return <LoadError thing="update preferences" error={settingsQ.error} />;
  }

  const change = (next: Partial<typeof current>) => {
    setSaved(false);
    save.mutate({ ...current, ...next }, { onSuccess: () => setSaved(true) });
  };
  const mode = UPDATE_MODES.find((option) => option.value === current.mode);

  return (
    <div className="mm-setgroup__rows">
      <SettingRow
        label="Check for updates"
        hint={
          current.check_for_updates
            ? "Weir looks for new versions on GitHub."
            : "Weir looks for nothing until you switch this on."
        }
      >
        <span className="mm-sys-switch">
          <MmOnOffSwitch
            id={CHECK_FOR_UPDATES_ID}
            label="Check for updates"
            layout="control"
            enabled={current.check_for_updates}
            disabled={save.isPending}
            onChange={(enabled) => change({ check_for_updates: enabled })}
          />
        </span>
      </SettingRow>
      {current.check_for_updates ? (
        <UpdateChoices
          current={current}
          mode={mode}
          pending={save.isPending}
          onChange={change}
        />
      ) : null}
      {save.isError ? (
        <p
          className="mm-status-text mm-sys-note"
          data-status="broken"
          role="alert"
        >
          {errorMessage(save.error, "Could not save update settings.")}
        </p>
      ) : null}
      {saved ? (
        <p
          className="mm-status-text mm-sys-note"
          data-status="done"
          role="status"
        >
          Update settings saved.
        </p>
      ) : null}
    </div>
  );
}

/** How updates are found and taken, shown while Weir is looking for them. */
function UpdateChoices({
  current,
  mode,
  pending,
  onChange,
}: {
  current: UpdateSettingsOut;
  mode: (typeof UPDATE_MODES)[number] | undefined;
  pending: boolean;
  onChange: (next: Partial<UpdateSettingsOut>) => void;
}) {
  return (
    <>
      <SettingRow
        label="Mode"
        hint={<span title={mode?.detail}>{mode?.summary}</span>}
      >
        <SegmentedControl
          ariaLabel="Update mode"
          options={MODE_OPTIONS}
          value={current.mode}
          onChange={(value) => onChange({ mode: value })}
        />
      </SettingRow>
      <SettingRow label="Check on startup">
        <span className="mm-sys-switch">
          <MmOnOffSwitch
            id={CHECK_ON_STARTUP_ID}
            label="Check for updates on startup"
            layout="control"
            enabled={current.check_on_startup}
            disabled={pending}
            onChange={(enabled) => onChange({ check_on_startup: enabled })}
          />
        </span>
      </SettingRow>
      <SettingRow label="Check every" htmlFor={CHECK_INTERVAL_ID}>
        <select
          id={CHECK_INTERVAL_ID}
          className="mm-input mm-sys-field"
          value={current.check_interval_minutes}
          disabled={pending}
          onChange={(event) =>
            onChange({ check_interval_minutes: Number(event.target.value) })
          }
        >
          {CHECK_INTERVALS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </SettingRow>
    </>
  );
}
