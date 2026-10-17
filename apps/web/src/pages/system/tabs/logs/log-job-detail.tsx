import { Link } from "react-router-dom";

import { errorMessage } from "../../../../lib/api/error-message";
import { usePauseQuery } from "../../../../lib/pause/pause-queries";
import {
  useProcessingJobCancelPendingMutation,
  useProcessingJobRecoverFinalizeFailedMutation,
} from "../../../../lib/processing/jobs-inspection/queries";
import type { SystemLogRow } from "../../../../lib/system/system-log-api";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { activityFileOfJob, formatPayload } from "./log-job-payload";
import { LOG_JOB_STATUSES } from "./log-filters";
import { LOG_ACTIONS, LogFacts, LogRawText } from "./log-facts";

type Job = NonNullable<SystemLogRow["job"]>;

const STATUS_WORDS = Object.fromEntries(
  LOG_JOB_STATUSES.map((status) => [status.value, status.label]),
);

/** What a person can do with a job from its row: take a queued one back, or save the result of one that finished its work. */
function JobActions({
  job,
  canAct,
  onChanged,
}: {
  job: Job;
  canAct: boolean;
  onChanged: () => void;
}) {
  const cancel = useProcessingJobCancelPendingMutation();
  const recover = useProcessingJobRecoverFinalizeFailedMutation();
  const failure = cancel.isError
    ? errorMessage(cancel.error, "Cancel failed.")
    : recover.isError
      ? errorMessage(recover.error, "Recovery failed.")
      : null;
  if (
    !canAct ||
    (job.status !== "pending" && job.status !== "handler_ok_finalize_failed")
  ) {
    return null;
  }
  return (
    <>
      {job.status === "pending" ? (
        <button
          type="button"
          className={mmActionButtonClass({ variant: "tertiary" })}
          disabled={cancel.isPending}
          data-testid={`processing-jobs-cancel-${job.id}`}
          onClick={() => cancel.mutate(job.id, { onSuccess: onChanged })}
        >
          Cancel pending
        </button>
      ) : (
        <button
          type="button"
          className={mmActionButtonClass({ variant: "secondary" })}
          disabled={recover.isPending}
          data-testid={`processing-jobs-recover-${job.id}`}
          onClick={() => recover.mutate(job.id, { onSuccess: onChanged })}
        >
          {recover.isPending ? "Recovering…" : "Recover result"}
        </button>
      )}
      {failure ? (
        <span
          className="mm-status-text text-sm"
          data-status="broken"
          role="alert"
        >
          {failure}
        </span>
      ) : null}
    </>
  );
}

/** An open job: what it is, how many times it has been tried, what it was given, why it stopped, and what can be done about it. */
export function LogJobDetail({
  job,
  canAct,
  time,
  onRelated,
  onChanged,
}: {
  job: Job;
  canAct: boolean;
  /** The job's last change in full, in Weir's time zone. */
  time: string;
  /** Narrows the log to this job and what mentions it. */
  onRelated: (jobId: number) => void;
  /** Called once the job has been cancelled or its result recovered, so the log shows it as it now is. */
  onChanged: () => void;
}) {
  const paused = usePauseQuery().data?.paused === true;
  const waitingForResume = paused && job.status === "pending";
  const file = activityFileOfJob(job);
  const payload = formatPayload(job.payload_json);
  return (
    <>
      <p className="mm-quiet-note">
        {waitingForResume
          ? "Waiting · Weir is paused."
          : job.operator_message || "This job needs a review."}
      </p>
      <p className="mm-quiet-note">
        <strong>Next step:</strong>{" "}
        {waitingForResume
          ? "Nothing to do. Resume at the top to continue."
          : job.next_action || "Open the related screen to inspect the job."}
      </p>
      <LogFacts
        facts={[
          { label: "Job", value: `#${job.id}` },
          { label: "Status", value: STATUS_WORDS[job.status] ?? job.status },
          {
            label: "Attempts",
            value: `${job.attempt_count} of ${job.max_attempts}`,
          },
          { label: "Last changed", value: time },
          { label: "Kind", value: job.job_kind, mono: true },
          { label: "Dedupe key", value: job.dedupe_key, mono: true },
          job.lease_owner && {
            label: "Worker lease",
            value: job.lease_owner,
            mono: true,
          },
        ]}
      />
      {payload ? <LogRawText label="What it was given" text={payload} /> : null}
      {job.last_error ? (
        <LogRawText label="Last error" text={job.last_error} />
      ) : null}
      <div className={LOG_ACTIONS}>
        <JobActions job={job} canAct={canAct} onChanged={onChanged} />
        {file ? (
          <Link className="mm-quiet-link" to={file}>
            Open the file in Activity →
          </Link>
        ) : null}
        <button
          type="button"
          className="mm-quiet-link"
          onClick={() => onRelated(job.id)}
        >
          Everything about this job →
        </button>
      </div>
    </>
  );
}
