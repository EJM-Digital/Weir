import { quietActionRowClass } from "../../../../components/shared/quiet-section";
import { errorMessage } from "../../../../lib/api/error-message";
import {
  writeFromProcessingLibrary,
  type ProcessingLibrary,
} from "../../../../lib/processing/libraries-api";
import { useUpdateProcessingLibrary } from "../../../../lib/processing/libraries-queries";
import { useProcessingWatchedFolderRemuxScanDispatchEnqueueMutation } from "../../../../lib/processing/queries";
import { useHandedOffNote } from "../../../../lib/processing/use-handed-off-note";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { everyWords } from "../cleanup/cleanup-jobs";
import { ScheduleGridEditor } from "./schedule-grid-editor";
import {
  DAY_NAMES,
  effectiveGrid,
  sameWeek,
  weekHours,
  windowNow,
} from "./schedule-model";

/**
 * A library's week at a glance: seven rows of 24 hours, lit where it may start work. A week with no
 * gaps is one lit bar, since seven identical rows say nothing more.
 */
function WeekStrip({ grid, name }: { grid: string; name: string }) {
  const week = weekHours(grid);
  const label = `${name}: when it may start work, Monday to Sunday`;
  if (week.every((day) => day.every(Boolean))) {
    return (
      <div className="mm-week mm-week--always" role="img" aria-label={label}>
        <span className="mm-week__hour is-on" />
      </div>
    );
  }
  return (
    <div className="mm-week" role="img" aria-label={label}>
      {week.map((day, d) => (
        <div className="mm-week__day" key={DAY_NAMES[d]}>
          <span className="mm-week__label">{DAY_NAMES[d].slice(0, 1)}</span>
          {day.map((on, h) => (
            <span
              key={h}
              className={on ? "mm-week__hour is-on" : "mm-week__hour"}
            />
          ))}
        </div>
      ))}
    </div>
  );
}

function RightNow({
  grid,
  zone,
  now,
}: {
  grid: string;
  zone: string;
  now: Date;
}) {
  const state = windowNow(grid, zone, now);
  switch (state.kind) {
    case "any":
      return <span>Any time</span>;
    case "never":
      return (
        <span className="mm-status-text" data-status="attention">
          Never: no hours are chosen
        </span>
      );
    case "open":
      return (
        <span>
          <span className="mm-status-text" data-status="done">
            Open
          </span>
          {state.until ? (
            <span className="mm-quiet-table__sub">until {state.until}</span>
          ) : null}
        </span>
      );
    case "closed":
      return (
        <span>
          <span className="mm-status-text" data-status="idle">
            Closed
          </span>
          <span className="mm-quiet-table__sub">opens {state.opens}</span>
        </span>
      );
  }
}

function ScanNowButton({
  library,
  editable,
}: {
  library: ProcessingLibrary;
  editable: boolean;
}) {
  const queueScan =
    useProcessingWatchedFolderRemuxScanDispatchEnqueueMutation();
  const watchedSet = Boolean(library.watched_folder.trim());
  return (
    <>
      <button
        type="button"
        aria-label={`Scan ${library.name} now`}
        className={mmActionButtonClass({ variant: "secondary", size: "row" })}
        disabled={!editable || !watchedSet || queueScan.isPending}
        title={
          watchedSet ? undefined : "This workflow has no watched folder yet."
        }
        onClick={() =>
          queueScan.mutate({
            enqueue_remux_jobs: true,
            media_scope: library.media_type,
            library_id: library.id,
          })
        }
      >
        {queueScan.isPending
          ? "Starting…"
          : queueScan.isSuccess
            ? "Scan queued"
            : "Scan now"}
      </button>
      {queueScan.isError ? (
        <span
          className="mm-status-text block text-xs"
          data-status="broken"
          role="alert"
        >
          {errorMessage(queueScan.error, "The scan could not be queued.")}
        </span>
      ) : null}
    </>
  );
}

/** One library's row: its week, whether it may start work now, and how often it looks. */
export function LibraryHoursRow({
  library,
  zone,
  now,
  editing,
  editable,
  onToggleEdit,
}: {
  library: ProcessingLibrary;
  zone: string;
  now: Date;
  editing: boolean;
  editable: boolean;
  onToggleEdit: () => void;
}) {
  const grid = effectiveGrid(library);
  const handedOff = useHandedOffNote(library.id);
  return (
    <tr
      className={editing ? "is-selected" : undefined}
      data-testid="schedule-library-row"
    >
      <th scope="row" className="mm-quiet-table__name">
        {library.name}
        {!library.enabled ? (
          <span className="mm-quiet-table__sub">Switched off in Workflows</span>
        ) : null}
      </th>
      <td data-label="Week">
        <WeekStrip grid={grid} name={library.name} />
      </td>
      <td data-label="Right now">
        <RightNow grid={grid} zone={zone} now={now} />
      </td>
      <td data-label="Looks for new files">
        {handedOff ?? `Every ${everyWords(library.scan_interval_seconds)}`}
      </td>
      <td data-label="">
        <div className="mm-schedule-actions">
          <button
            type="button"
            className={mmActionButtonClass({
              variant: "secondary",
              size: "row",
            })}
            aria-expanded={editing}
            onClick={onToggleEdit}
          >
            Change hours
          </button>
          {handedOff ? null : (
            <ScanNowButton library={library} editable={editable} />
          )}
        </div>
      </td>
    </tr>
  );
}

/**
 * The hours editor for one library, under the table. Saving it is the whole of that library's
 * schedule. The drawn week is held by the section, so it can ask before another library's hours
 * replace unsaved ones.
 */
export function LibraryHoursEditor({
  library,
  grid,
  onGrid,
  editable,
  onSaved,
  onClose,
}: {
  library: ProcessingLibrary;
  grid: string;
  onGrid: (grid: string) => void;
  editable: boolean;
  onSaved: () => void;
  onClose: () => void;
}) {
  const update = useUpdateProcessingLibrary();
  const dirty = !sameWeek(grid, effectiveGrid(library));

  const save = () =>
    update.mutate(
      {
        id: library.id,
        data: {
          ...writeFromProcessingLibrary(library),
          // The drawn week is the schedule from now on: the older days-and-hours window is retired so the
          // two can never disagree about when this library runs.
          schedule_grid: grid,
          schedule_enabled: true,
          schedule_hours_limited: false,
        },
      },
      { onSuccess: onSaved },
    );

  return (
    <div
      className="mm-schedule-editor"
      data-testid="schedule-library-editor"
      aria-label={`${library.name} hours`}
    >
      <h4 className="mm-schedule-editor__title">
        {library.name}: when it may start work
      </h4>
      <ScheduleGridEditor
        value={grid}
        onChange={onGrid}
        disabled={!editable || update.isPending}
      />
      {update.isError ? (
        <p
          className="mm-status-text mt-2 text-sm"
          data-status="broken"
          role="alert"
        >
          {errorMessage(update.error, "These hours could not be saved.")}
        </p>
      ) : null}
      <div className={`${quietActionRowClass} mt-4`}>
        <button
          type="button"
          className={mmActionButtonClass({ variant: "primary" })}
          disabled={!editable || !dirty || update.isPending}
          data-testid="schedule-library-save"
          onClick={save}
        >
          {update.isPending ? "Saving…" : `Save ${library.name}'s hours`}
        </button>
        <button
          type="button"
          className={mmActionButtonClass({ variant: "tertiary" })}
          onClick={onClose}
        >
          Close
        </button>
      </div>
    </div>
  );
}
