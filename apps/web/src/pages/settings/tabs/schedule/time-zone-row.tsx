import { useEffect, useMemo, useState } from "react";

import { MmListboxPicker } from "../../../../components/ui/mm-listbox-picker";
import { errorMessage } from "../../../../lib/api/error-message";
import { useAppSettingsSaveMutation } from "../../../../lib/settings/queries";
import { timezoneOptionsFor } from "../../../../lib/settings/timezone-options";
import type { AppSettings } from "../../../../lib/settings/types";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { DAY_NAMES, zoneClock } from "./schedule-model";

const FALLBACK_ZONE = "UTC";

const twoDigits = (value: number) => String(value).padStart(2, "0");

/** The zone Weir reads every time in: the saved one, or UTC when none is saved. */
export function savedZone(settings: AppSettings): string {
  return (settings.app_timezone || "").trim() || FALLBACK_ZONE;
}

/**
 * The time zone every time in Weir is read in: the picker with a Save that appears once a different zone is chosen,
 * and the time it is there now. The parent keys this row on the saved zone, so a save elsewhere resets the choice.
 */
export function TimeZoneRow({
  labelId,
  settings,
  editable,
  now,
  onUnsavedChange,
}: {
  /** The id of the visible text that names this setting. */
  labelId: string;
  settings: AppSettings;
  editable: boolean;
  now: Date;
  /** Told whether a zone is chosen and not yet saved, so the page can ask before it is left. */
  onUnsavedChange?: (unsaved: boolean) => void;
}) {
  const save = useAppSettingsSaveMutation();
  const saved = savedZone(settings);
  const [zone, setZone] = useState(saved);
  const options = useMemo(() => timezoneOptionsFor(saved), [saved]);
  const clock = zoneClock(now, saved);
  const dirty = zone !== saved;

  useEffect(() => {
    onUnsavedChange?.(dirty);
    return () => onUnsavedChange?.(false);
  }, [dirty, onUnsavedChange]);

  return (
    <>
      <div className="mm-schedule-zone">
        <MmListboxPicker
          ariaLabelledBy={labelId}
          placeholder="Select time zone"
          disabled={!editable || save.isPending}
          options={options.map((tz) => ({ value: tz.id, label: tz.label }))}
          value={zone}
          onChange={setZone}
        />
        {dirty ? (
          <button
            type="button"
            className={`${mmActionButtonClass({ variant: "primary" })} mm-sys-btn`}
            disabled={!editable || save.isPending}
            data-testid="schedule-save-timezone"
            onClick={() =>
              save.mutate({
                setup_wizard_state: settings.setup_wizard_state,
                app_timezone: zone,
                log_retention_days: settings.log_retention_days,
              })
            }
          >
            {save.isPending ? "Saving…" : "Save"}
          </button>
        ) : null}
      </div>
      <p className="mm-sys-note">
        Now {DAY_NAMES[clock.weekday]} {twoDigits(clock.hour)}:
        {twoDigits(clock.minute)}. Every time across Weir is in this zone.
      </p>
      {save.isError ? (
        <p
          className="mm-status-text mt-2 text-sm"
          data-status="broken"
          role="alert"
        >
          {errorMessage(save.error, "The time zone could not be saved.")}
        </p>
      ) : null}
    </>
  );
}
