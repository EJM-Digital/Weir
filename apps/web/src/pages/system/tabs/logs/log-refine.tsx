import { useId, type ReactNode } from "react";

import { Chip } from "../../../../components/panels/chip";
import { MmListboxPicker } from "../../../../components/ui/mm-listbox-picker";
import { MmMultiListboxPicker } from "../../../../components/ui/mm-multi-listbox-picker";
import {
  ACTIVITY_RESULT_LABELS,
  ACTIVITY_TRIGGER_LABELS,
} from "../../../../lib/activity/activity-runs";
import { LogChips } from "./log-chips";
import { LOG_JOB_STATUSES, type LogFilters } from "./log-filters";

const withAny = (
  any: string,
  entries: readonly (readonly [value: string, label: string])[],
) => [
  { value: "", label: any },
  ...entries.map(([value, label]) => ({ value, label })),
];

function RefinePicker({
  label,
  testId,
  children,
}: {
  label: string;
  testId?: string;
  children: (labelId: string) => ReactNode;
}) {
  const labelId = useId();
  return (
    <div className="mm-workflow-picker" data-testid={testId}>
      <span id={labelId} className="sr-only">
        {label}
      </span>
      {children(labelId)}
    </div>
  );
}

/** What a job status picker says when closed: any, one named, or how many. */
function statusSummary(
  statuses: readonly (typeof LOG_JOB_STATUSES)[number][],
): string {
  if (statuses.length === 0) return "Any job status";
  if (statuses.length === 1) return statuses[0].label;
  return `${statuses.length} job statuses`;
}

/**
 * The filters only some rows have, which the header has no room for: an event's type, result and cause, a job's status, a
 * server line's stack trace, and one job with everything that mentions it. Setting one leaves out the sources that do not
 * have it, and the counts in the header say so.
 */
export function LogRefine({
  filters,
  eventTypes,
  onChange,
}: {
  filters: LogFilters;
  /** The event types the list holds, to choose from. */
  eventTypes: readonly { value: string; label: string }[];
  onChange: (next: Partial<LogFilters>) => void;
}) {
  return (
    <div className="mm-log-toolbar" data-testid="logs-refine">
      <RefinePicker label="Event type" testId="logs-event-type">
        {(labelId) => (
          <MmListboxPicker
            options={withAny(
              "Any event",
              eventTypes.map((t) => [t.value, t.label]),
            )}
            value={filters.eventType}
            onChange={(eventType) => onChange({ eventType })}
            ariaLabelledBy={labelId}
          />
        )}
      </RefinePicker>
      <RefinePicker label="Result">
        {(labelId) => (
          <MmListboxPicker
            options={withAny(
              "Any result",
              Object.entries(ACTIVITY_RESULT_LABELS),
            )}
            value={filters.result}
            onChange={(result) => onChange({ result })}
            ariaLabelledBy={labelId}
          />
        )}
      </RefinePicker>
      <RefinePicker label="Why it happened">
        {(labelId) => (
          <MmListboxPicker
            options={withAny(
              "Any cause",
              Object.entries(ACTIVITY_TRIGGER_LABELS),
            )}
            value={filters.trigger}
            onChange={(trigger) => onChange({ trigger })}
            ariaLabelledBy={labelId}
          />
        )}
      </RefinePicker>
      <RefinePicker label="Job status" testId="logs-job-status">
        {(labelId) => (
          <MmMultiListboxPicker
            options={LOG_JOB_STATUSES}
            values={filters.statuses}
            summaryText={statusSummary(
              LOG_JOB_STATUSES.filter((status) =>
                filters.statuses.includes(status.value),
              ),
            )}
            onChange={(statuses) => onChange({ statuses })}
            ariaLabelledBy={labelId}
          />
        )}
      </RefinePicker>
      <LogChips
        ariaLabel="Server lines"
        chips={[
          {
            value: "stack",
            label: "Stack traces only",
            pressed: filters.stackOnly,
          },
        ]}
        onToggle={() => onChange({ stackOnly: !filters.stackOnly })}
      />
      {filters.job === null ? null : (
        <Chip dot={false} data-testid="logs-job-filter">
          Everything about job #{filters.job}
          <button
            type="button"
            className="mm-quiet-link ml-2"
            onClick={() => onChange({ job: null })}
          >
            Show all
          </button>
        </Chip>
      )}
    </div>
  );
}
