import type { ReactNode } from "react";

import { FileProgressDetail } from "../../../../components/activity/file-progress-detail";
import { RemuxPassDetail } from "../../../../components/activity/remux-pass-detail";
import { StructuredDetailFacts } from "../../../../components/activity/structured-detail-facts";
import {
  eventDisplay,
  inlineDetailOf,
} from "../../../../lib/activity/activity-display";
import {
  ACTIVITY_RESULT_LABELS,
  activityTriggerLabel,
} from "../../../../lib/activity/activity-runs";
import { asNumber, parseActivityDetail } from "../../../../lib/activity/detail";
import {
  FILE_PROGRESS_EVENT,
  REMUX_PASS_COMPLETED_EVENT,
} from "../../../../lib/activity/event-types";
import type { ActivityEventItem } from "../../../../lib/api/types";
import { LOG_ACTIONS, LogFacts } from "./log-facts";

/** The job an event's detail names, when it does: the way from an event to the job that wrote it. */
export function jobOfEvent(ev: ActivityEventItem): number | null {
  return asNumber(parseActivityDetail(ev.detail)?.job_id) ?? null;
}

/** What an event's detail opens to: a card for a processing pass, the facts of the detail otherwise. */
function EventBody({
  ev,
  detail,
}: {
  ev: ActivityEventItem;
  detail: string;
}): ReactNode {
  if (ev.event_type === FILE_PROGRESS_EVENT) {
    return <FileProgressDetail detail={detail} />;
  }
  if (ev.event_type === REMUX_PASS_COMPLETED_EVENT) {
    return <RemuxPassDetail detail={detail} />;
  }
  return <StructuredDetailFacts detail={detail} />;
}

/** An open Activity event: why it happened, how it went, and everything its record holds. */
export function LogEventDetail({
  ev,
  time,
  onRelated,
}: {
  ev: ActivityEventItem;
  /** The event's time in full, in Weir's time zone. */
  time: string;
  /** Narrows the log to a job and what mentions it. */
  onRelated: (jobId: number) => void;
}) {
  const display = eventDisplay(ev);
  const jobId = jobOfEvent(ev);
  const summary = [display.summary, inlineDetailOf(ev, display)]
    .filter(Boolean)
    .join(" · ");
  return (
    <>
      <p className="mm-quiet-note">{summary}</p>
      <LogFacts
        facts={[
          { label: "When", value: time },
          { label: "Why it happened", value: activityTriggerLabel(ev.trigger) },
          {
            label: "Result",
            value: ev.result
              ? (ACTIVITY_RESULT_LABELS[ev.result] ?? ev.result)
              : null,
          },
          { label: "Event", value: ev.event_type, mono: true },
        ]}
      />
      {display.detail && !inlineDetailOf(ev, display) ? (
        <div>
          <EventBody ev={ev} detail={display.detail} />
        </div>
      ) : null}
      {jobId !== null ? (
        <div className={LOG_ACTIONS}>
          <button
            type="button"
            className="mm-quiet-link"
            onClick={() => onRelated(jobId)}
          >
            Everything about job #{jobId} →
          </button>
        </div>
      ) : null}
    </>
  );
}
