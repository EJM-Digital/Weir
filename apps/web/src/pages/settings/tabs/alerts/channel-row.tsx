import { Fragment, type ReactNode } from "react";

import type { NotificationChannelOut } from "../../../../lib/settings/types";
import { Chip } from "../../../../components/panels/chip";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import {
  ALERT_ACTIONS_COLUMN,
  ALERT_NAME_COLUMN,
  eventColumnId,
} from "./alert-columns";
import { eventLabel } from "./alert-events";
import { ALERT_CHECKBOX_CLASS } from "./channel-form";

export type TestResult = { ok: boolean; error: string | null };

function TestOutcome({ result }: { result: TestResult }) {
  return (
    <p
      className="mm-quiet-table__sub mm-status-text"
      data-status={result.ok ? "done" : "broken"}
      role="alert"
    >
      {result.ok
        ? "Test alert sent successfully."
        : `Test failed: ${result.error ?? "Unknown error"}`}
    </p>
  );
}

/** One channel as a table row. The hairline under it is what says a Remove button
 *  belongs to this channel and not its neighbour. */
export function ChannelRow({
  channel,
  events,
  order,
  onToggleEvent,
  toggling,
  onEdit,
  onDelete,
  onTest,
  testing,
  testResult,
  deleting,
}: {
  channel: NotificationChannelOut;
  events: string[];
  /** The ids of the table's columns, in the order to show them. */
  order: readonly string[];
  onToggleEvent: (event: string) => void;
  toggling: boolean;
  onEdit: () => void;
  onDelete: () => void;
  onTest: () => void;
  testing: boolean;
  testResult: TestResult | null;
  deleting: boolean;
}) {
  const cells: Record<string, ReactNode> = {
    [ALERT_NAME_COLUMN]: (
      <th
        scope="row"
        data-col={ALERT_NAME_COLUMN}
        className="mm-quiet-table__name"
      >
        <span>{channel.label}</span>
        <Chip dot={false}>{channel.provider}</Chip>
        {!channel.enabled ? <Chip meaning="idle">Disabled</Chip> : null}
        <span className="mm-quiet-table__sub font-mono">{channel.url}</span>
      </th>
    ),
    [ALERT_ACTIONS_COLUMN]: (
      <td data-label="" data-col={ALERT_ACTIONS_COLUMN}>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            className={mmActionButtonClass({
              variant: "tertiary",
              size: "row",
            })}
            disabled={testing || deleting}
            onClick={onTest}
          >
            {testing ? "Testing…" : "Send test"}
          </button>
          <button
            type="button"
            className={mmActionButtonClass({
              variant: "secondary",
              size: "row",
            })}
            disabled={deleting}
            onClick={onEdit}
          >
            Edit
          </button>
          <button
            type="button"
            className={mmActionButtonClass({
              variant: "danger-outline",
              size: "row",
            })}
            disabled={deleting}
            aria-haspopup="dialog"
            onClick={onDelete}
          >
            {deleting ? "Removing…" : "Remove"}
          </button>
        </div>
        {testResult ? <TestOutcome result={testResult} /> : null}
      </td>
    ),
  };
  for (const event of events) {
    cells[eventColumnId(event)] = (
      <td
        data-label={eventLabel(event)}
        data-col={eventColumnId(event)}
        className="mm-alerts-cell"
      >
        <input
          type="checkbox"
          className={ALERT_CHECKBOX_CLASS}
          aria-label={`${channel.label}: ${eventLabel(event)}`}
          checked={channel.events.includes(event)}
          disabled={toggling}
          onChange={() => onToggleEvent(event)}
        />
      </td>
    );
  }
  return (
    <tr>
      {order.map((id) => (
        <Fragment key={id}>{cells[id]}</Fragment>
      ))}
    </tr>
  );
}
