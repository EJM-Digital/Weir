import { useState, type ReactNode } from "react";

import { quietActionRowClass } from "../../../../components/shared/quiet-section";
import { errorMessage } from "../../../../lib/api/error-message";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import type { SystemSettingsForm } from "../../use-system-settings-form";
import type { FileActivityRetention } from "./use-file-activity-retention";

const MAX_LOG_DAYS = 3650;

/** One retention setting: what is kept, how many days, and a line on what the number does. */
function RetentionRow({
  inputId,
  label,
  help,
  detail,
  children,
}: {
  inputId: string;
  label: string;
  help: ReactNode;
  /** The help in full, for a hover note. */
  detail?: string;
  children: ReactNode;
}) {
  return (
    <div className="mm-retention__row">
      <label className="mm-retention__label" htmlFor={inputId}>
        {label}
      </label>
      <span className="mm-retention__days">
        {children}
        <span>days</span>
      </span>
      <span className="mm-retention__help" title={detail}>
        {help}
      </span>
    </div>
  );
}

function FileActivityRow({
  fileActivity,
  disabled,
}: {
  fileActivity: FileActivityRetention;
  disabled: boolean;
}) {
  const inputId = "retention-file-activity";
  if (fileActivity.status === "unreadable") {
    return (
      <p
        className="mm-status-text mm-sys-note"
        data-status="broken"
        role="alert"
      >
        Weir could not read how long a file&rsquo;s activity is kept. Refresh
        the page to try again.
      </p>
    );
  }
  return (
    <RetentionRow
      inputId={inputId}
      label="File activity"
      help="Kept after the file is gone. 0 keeps it for ever."
      detail="While Weir still knows a file, its activity is kept. This is how many days it is kept once the file is gone or forgotten. 0 keeps it for ever."
    >
      <input
        id={inputId}
        type="number"
        min={0}
        max={fileActivity.maxDays}
        className="mm-input mm-retention__number"
        value={fileActivity.shown}
        disabled={disabled || fileActivity.status !== "ready"}
        onChange={(e) => fileActivity.setDraft(e.target.value)}
      />
    </RetentionRow>
  );
}

/**
 * How long everything Weir records is kept: the system log, Activity, and a file's activity. One form: the three
 * numbers are edited together and saved by one button, which appears once one of them has changed.
 */
export function RetentionSection({
  form,
  fileActivity,
  editable,
}: {
  form: SystemSettingsForm;
  /** The file activity number, held by whoever shows this, so a draft outlives the panel closing. */
  fileActivity: FileActivityRetention;
  editable: boolean;
}) {
  const { retention, save } = form;
  const [saved, setSaved] = useState(false);
  const saving = save.isPending || fileActivity.saving;
  const disabled = !editable || saving;
  const dirty = retention.dirty || fileActivity.dirty;
  const failed = save.isError && form.lastSaveTarget === "logs";

  const saveAll = () => {
    setSaved(false);
    if (retention.dirty) {
      form.saveFrom("logs", { onSaved: () => setSaved(true) });
    }
    if (fileActivity.dirty) fileActivity.save(() => setSaved(true));
  };

  return (
    <section
      aria-labelledby="suite-settings-log-retention-heading"
      data-testid="suite-settings-retention"
    >
      <h3
        id="suite-settings-log-retention-heading"
        className="mm-drawer__eyebrow"
      >
        How long things are kept
      </h3>
      <div className="mm-retention">
        <RetentionRow
          inputId="retention-system-log"
          label="System log"
          help="1 to 3650. Older entries are removed while Weir runs."
        >
          <input
            id="retention-system-log"
            type="number"
            min={1}
            max={MAX_LOG_DAYS}
            className="mm-input mm-retention__number"
            value={retention.logValue}
            disabled={disabled}
            onChange={(e) => retention.setLogDraft(e.target.value)}
            onBlur={() =>
              retention.setLogDraft(String(retention.finalizeLogDays()))
            }
          />
        </RetentionRow>
        <RetentionRow
          inputId="retention-activity"
          label="Events"
          help="0 keeps them until you clear them. Media files are never touched."
        >
          <input
            id="retention-activity"
            type="number"
            min={0}
            max={MAX_LOG_DAYS}
            className="mm-input mm-retention__number"
            value={retention.activityValue}
            disabled={disabled}
            data-testid="suite-settings-activity-retention"
            onChange={(e) => retention.setActivityDraft(e.target.value)}
            onBlur={() =>
              retention.setActivityDraft(
                String(retention.finalizeActivityDays()),
              )
            }
          />
        </RetentionRow>
        <FileActivityRow fileActivity={fileActivity} disabled={disabled} />
      </div>
      {dirty || failed || fileActivity.error || saved ? (
        <div className={quietActionRowClass}>
          {dirty ? (
            <button
              type="button"
              className={`${mmActionButtonClass({ variant: "primary" })} mm-sys-btn`}
              disabled={
                !editable ||
                saving ||
                (fileActivity.dirty && !fileActivity.valid)
              }
              data-testid="suite-settings-save-logs"
              onClick={saveAll}
            >
              {saving ? "Saving…" : "Save retention"}
            </button>
          ) : null}
          {failed ? (
            <p
              className="mm-status-text text-sm"
              data-status="broken"
              role="alert"
              data-testid="suite-settings-logs-save-error"
            >
              {errorMessage(save.error, "Could not save.")}
            </p>
          ) : fileActivity.error ? (
            <p
              className="mm-status-text text-sm"
              data-status="broken"
              role="alert"
            >
              {errorMessage(
                fileActivity.error,
                "That change could not be saved.",
              )}
            </p>
          ) : saved && !dirty ? (
            <p
              className="mm-status-text text-sm"
              data-status="done"
              role="status"
            >
              Retention saved.
            </p>
          ) : null}
        </div>
      ) : null}
    </section>
  );
}
