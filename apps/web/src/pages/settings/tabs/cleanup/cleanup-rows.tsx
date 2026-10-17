import { Fragment, useState, type ReactNode } from "react";

import { MmOnOffSwitch } from "../../../../components/ui/mm-on-off-switch";
import type { MaintenanceFamilyState } from "../../../../lib/processing/maintenance-api";
import type { ProcessingOperatorSettingsPutBody } from "../../../../lib/processing/types";
import { classNames } from "../../../../lib/ui/class-names";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { useAppDateFormatter } from "../../../../lib/ui/mm-format-date";
import type { CleanupConfirmAction } from "./cleanup-confirm-dialog";
import type { CleanupColumnId } from "./cleanup-columns";
import {
  choiceLabel,
  DEFAULT_INTERVAL_SECONDS,
  everyChoices,
  everyWords,
  lastRunLine,
  type CleanupJob,
} from "./cleanup-jobs";

/** Saves one operator setting and says in words what changed. Resolves to whether it was saved. */
export type SaveSetting = (
  body: ProcessingOperatorSettingsPutBody,
  said: string,
) => Promise<boolean>;

function nextRunWords(
  state: MaintenanceFamilyState,
  formatDate: (iso: string) => string,
): string {
  if (!state.enabled) return "Off";
  return state.next_run_at
    ? formatDate(state.next_run_at)
    : "Within half a minute";
}

/** One cleanup job: its switch, its timer, when it last and next runs, and Run now, in the order of the columns. */
export function CleanupJobRow({
  job,
  state,
  order,
  switchId,
  editable,
  saving,
  running,
  onSave,
  onRun,
  onRequestConfirm,
}: {
  job: CleanupJob;
  state: MaintenanceFamilyState;
  order: readonly CleanupColumnId[];
  switchId: string;
  editable: boolean;
  saving: boolean;
  running: boolean;
  onSave: SaveSetting;
  onRun: () => void;
  /** Asks for confirmation before a destructive job is switched on or run now. */
  onRequestConfirm: (action: CleanupConfirmAction) => void;
}) {
  const formatDate = useAppDateFormatter();
  const interval = state.interval_seconds ?? DEFAULT_INTERVAL_SECONDS;
  const cells: Record<CleanupColumnId, ReactNode> = {
    job: (
      <th data-col="job" scope="row" className="mm-quiet-table__name">
        <span>{job.name}</span>
        <span
          className={classNames(
            "mm-cleanup-what",
            job.destructive && "mm-status-text",
          )}
          data-status={job.destructive ? "attention" : undefined}
        >
          {state.description}
        </span>
      </th>
    ),
    on: (
      <td data-col="on" data-label="On">
        <MmOnOffSwitch
          id={switchId}
          label={`${job.name} on`}
          enabled={state.enabled}
          disabled={!editable || saving}
          layout="control"
          size="row"
          onChange={(on) => {
            if (on && job.destructive) {
              onRequestConfirm("enable");
              return;
            }
            void onSave(
              { [job.enabledField]: on },
              on
                ? `${job.name} is on. It first runs within half a minute.`
                : `${job.name} is off.`,
            );
          }}
        />
      </td>
    ),
    every: (
      <td data-col="every" data-label="Every">
        <select
          className="mm-input mm-cleanup-every"
          aria-label={`How often ${job.name} runs`}
          value={interval}
          disabled={!editable || saving}
          onChange={(event) => {
            const seconds = Number(event.target.value);
            void onSave(
              { [job.intervalField]: seconds },
              `${job.name} now runs every ${everyWords(seconds)}.`,
            );
          }}
        >
          {everyChoices(interval).map((choice) => (
            <option key={choice.seconds} value={choice.seconds}>
              {choiceLabel(choice.label)}
            </option>
          ))}
        </select>
      </td>
    ),
    lastRun: (
      <td data-label="Last run" data-col="lastRun">
        {lastRunLine(state, formatDate)}
      </td>
    ),
    nextRun: (
      <td data-label="Next run" data-col="nextRun">
        {nextRunWords(state, formatDate)}
      </td>
    ),
    runNow: (
      <td data-col="runNow" data-label="Run now">
        <button
          type="button"
          className={mmActionButtonClass({
            variant: "tertiary",
            size: "row",
          })}
          disabled={running}
          onClick={() => (job.destructive ? onRequestConfirm("run") : onRun())}
          data-testid={`processing-maintenance-run-${job.family}`}
        >
          Run now
        </button>
      </td>
    ),
  };
  return (
    <tr data-testid={`processing-maintenance-${job.family}`}>
      {order.map((id) => (
        <Fragment key={id}>{cells[id]}</Fragment>
      ))}
    </tr>
  );
}

/**
 * A setting in the same table as the jobs it belongs beside. It is one cell across the whole row, its name and what it
 * does first and its control under them, so it reads the same whichever order the job columns are in.
 */
function SettingRow({
  testId,
  name,
  description,
  columnCount,
  children,
}: {
  testId: string;
  name: string;
  description: string;
  /** How many columns the table is showing, which the setting's cell spans. */
  columnCount: number;
  children: ReactNode;
}) {
  return (
    <tr data-testid={testId}>
      <td colSpan={columnCount} className="mm-cleanup-setting">
        <span className="mm-quiet-table__name">{name}</span>
        <span className="mm-cleanup-what">{description}</span>
        <div className="mm-cleanup-setting__control">{children}</div>
      </td>
    </tr>
  );
}

/**
 * A setting counted in days, in the same table as the jobs it governs. Save appears once the value
 * differs from the saved one and is within range.
 */
export function DaysSettingRow({
  testId,
  name,
  description,
  columnCount,
  inputId,
  inputLabel,
  saved,
  min,
  max,
  editable,
  saving,
  savedWords,
  onSave,
  toBody,
}: {
  testId: string;
  name: string;
  description: string;
  /** How many columns the table is showing. */
  columnCount: number;
  inputId: string;
  inputLabel: string;
  saved: number | undefined;
  min: number;
  max: number;
  editable: boolean;
  saving: boolean;
  savedWords: (days: number) => string;
  onSave: SaveSetting;
  toBody: (days: number) => ProcessingOperatorSettingsPutBody;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  const shown = draft ?? String(saved ?? "");
  const value = Number.parseInt(shown, 10);
  const dirty = saved !== undefined && draft !== null && value !== saved;
  const inRange = value >= min && value <= max;
  return (
    <SettingRow
      testId={testId}
      name={name}
      description={description}
      columnCount={columnCount}
    >
      <span className="mm-setrow__unit">
        <label className="sr-only" htmlFor={inputId}>
          {inputLabel}
        </label>
        <input
          id={inputId}
          className="mm-input mm-setrow__number"
          type="number"
          min={min}
          max={max}
          value={shown}
          disabled={!editable || saving}
          onChange={(event) => setDraft(event.target.value)}
        />
        days
        {dirty && inRange ? (
          <button
            type="button"
            className={mmActionButtonClass({
              variant: "secondary",
              size: "row",
            })}
            disabled={saving}
            onClick={() =>
              void onSave(toBody(value), savedWords(value)).then((saved) => {
                if (saved) setDraft(null);
              })
            }
          >
            {saving ? "Saving…" : "Save"}
          </button>
        ) : null}
      </span>
    </SettingRow>
  );
}

/** A yes-or-no setting in the same table as the jobs it belongs beside; it saves the moment it is switched. */
export function SwitchSettingRow({
  testId,
  name,
  description,
  columnCount,
  switchId,
  enabled,
  editable,
  saving,
  onChange,
}: {
  testId: string;
  name: string;
  description: string;
  /** How many columns the table is showing. */
  columnCount: number;
  switchId: string;
  enabled: boolean;
  editable: boolean;
  saving: boolean;
  onChange: (on: boolean) => void;
}) {
  return (
    <SettingRow
      testId={testId}
      name={name}
      description={description}
      columnCount={columnCount}
    >
      <MmOnOffSwitch
        id={switchId}
        label={name}
        enabled={enabled}
        disabled={!editable || saving}
        layout="control"
        size="row"
        onChange={onChange}
      />
    </SettingRow>
  );
}
