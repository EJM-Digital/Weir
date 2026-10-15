import { useRef, useState } from "react";

import { ConfirmDialog } from "../../../../components/ui/confirm-dialog";
import { ModalDialog } from "../../../../components/ui/modal-dialog";
import { errorMessage } from "../../../../lib/api/error-message";
import type {
  ConnectionInUse,
  ConnectionWorkflow,
  MediaManagerConnection,
} from "../../../../lib/media-managers/media-managers-api";
import {
  useDeleteMediaManagerConnection,
  useGiveUpMediaManagerReports,
  useRemoveWorkflows,
  useUnlinkMediaManagerWorkflows,
} from "../../../../lib/media-managers/queries";
import { connectionTitle } from "../../../../lib/ui/connection-title";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";

const UNLINK_FAILURE = "Could not unlink those workflows.";
const GIVE_UP_FAILURE = "Could not give up on those reports.";

/** "Movies", "Movies and TV Shows", "Movies, TV Shows and Anime". */
function namesOf(workflows: readonly ConnectionWorkflow[]): string {
  const names = workflows.map((workflow) => workflow.name);
  return names.length < 2
    ? names.join("")
    : `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
}

/** What the person chose to do about the workflows, and how it went; shown above the question when it is asked again. */
type StepOutcome = { kind: "done" | "failed"; text: string };

/**
 * Removing a media manager connection. Asking first is the whole dialog until Weir says something still depends on the
 * connection (workflows that come from it, hand-offs in progress, reports owed): then it lists what, and the person chooses,
 * as separate steps, to unlink the workflows or to remove them. Nothing changes until one of them is chosen, and after a step
 * the question is asked again.
 */
export function RemoveConnectionDialog({
  connection,
  remove,
  onClose,
}: {
  connection: MediaManagerConnection;
  remove: ReturnType<typeof useDeleteMediaManagerConnection>;
  onClose: () => void;
}) {
  const unlink = useUnlinkMediaManagerWorkflows();
  const removeWorkflows = useRemoveWorkflows();
  const giveUp = useGiveUpMediaManagerReports();
  const [inUse, setInUse] = useState<ConnectionInUse | null>(null);
  const [outcome, setOutcome] = useState<StepOutcome | null>(null);
  const title = connectionTitle(connection);

  const close = () => {
    remove.reset();
    onClose();
  };

  if (inUse) {
    return (
      <InUseDialog
        title={title}
        inUse={inUse}
        busy={unlink.isPending || removeWorkflows.isPending || giveUp.isPending}
        onKeep={close}
        onUnlink={() =>
          unlink.mutate(connection.id, {
            onSuccess: (result) => {
              setOutcome({
                kind: "done",
                text: `${namesOf(result.workflows)} ${result.workflows.length === 1 ? "is" : "are"} no longer linked to ${title}. They stay as they are; a workflow no media manager feeds any more is switched off until you choose what it does next.`,
              });
              setInUse(null);
            },
            onError: (error) => {
              setOutcome({
                kind: "failed",
                text: errorMessage(error, UNLINK_FAILURE),
              });
              setInUse(null);
            },
          })
        }
        onGiveUpReports={() =>
          giveUp.mutate(connection.id, {
            onSuccess: (result) => {
              setOutcome({
                kind: "done",
                text: `Weir gave up on ${result.reports.length === 1 ? "the report" : "the reports"} it owed ${title}. Activity records ${result.reports.length === 1 ? "it" : "them"}.`,
              });
              setInUse(null);
            },
            onError: (error) => {
              setOutcome({
                kind: "failed",
                text: errorMessage(error, GIVE_UP_FAILURE),
              });
              setInUse(null);
            },
          })
        }
        onRemoveWorkflows={() =>
          removeWorkflows.mutate(inUse.workflows, {
            onSuccess: () => {
              setOutcome({
                kind: "done",
                text: `${namesOf(inUse.workflows)} ${inUse.workflows.length === 1 ? "was" : "were"} removed. The folders and files ${inUse.workflows.length === 1 ? "it" : "they"} watched were not touched.`,
              });
              setInUse(null);
            },
            onError: (error) => {
              setOutcome({
                kind: "failed",
                text: errorMessage(error, "Could not remove those workflows."),
              });
              setInUse(null);
            },
          })
        }
      />
    );
  }

  return (
    <ConfirmDialog
      testId="media-manager-remove-confirm"
      title={`Remove ${title}?`}
      description={
        <>
          {outcome ? (
            <p
              data-testid="media-manager-remove-outcome"
              role={outcome.kind === "failed" ? "alert" : "status"}
              className={
                outcome.kind === "failed" ? "mm-status-text" : undefined
              }
              data-status={outcome.kind === "failed" ? "broken" : undefined}
            >
              {outcome.text}
            </p>
          ) : null}
          <p>
            Weir will stop accepting files from {title}. Its address, API key
            and webhook secret go with it, so connecting it again means setting
            it up from scratch.
          </p>
          <p>No media file is touched. This cannot be undone.</p>
        </>
      }
      confirmLabel="Remove media manager"
      tone="danger"
      busy={remove.isPending}
      error={
        remove.isError
          ? errorMessage(remove.error, "Could not remove this media manager.")
          : null
      }
      onCancel={close}
      onConfirm={() => {
        setOutcome(null);
        remove.mutate(connection.id, {
          onSuccess: (refusal) => {
            if (refusal) setInUse(refusal);
            else onClose();
          },
        });
      }}
    />
  );
}

function InUseDialog({
  title,
  inUse,
  busy,
  onKeep,
  onUnlink,
  onRemoveWorkflows,
  onGiveUpReports,
}: {
  title: string;
  inUse: ConnectionInUse;
  busy: boolean;
  onKeep: () => void;
  onUnlink: () => void;
  onRemoveWorkflows: () => void;
  onGiveUpReports: () => void;
}) {
  const keepRef = useRef<HTMLButtonElement>(null);
  const hasWorkflows = inUse.workflows.length > 0;
  const waiting = inUse.handoffs_in_progress > 0;
  const owed = inUse.reports.length;
  return (
    <ModalDialog
      title={`${title} is still needed`}
      testId="media-manager-in-use"
      onClose={onKeep}
      busy={busy}
      initialFocus={keepRef}
    >
      <div className="mm-modal__body space-y-2">
        <p data-testid="media-manager-in-use-detail">{inUse.detail}</p>
        {hasWorkflows ? (
          <>
            <ul
              data-testid="media-manager-in-use-workflows"
              className="list-disc pl-5"
            >
              {inUse.workflows.map((workflow) => (
                <li key={workflow.id}>{workflow.name}</li>
              ))}
            </ul>
            <p>
              <strong>Unlink them</strong> and they stay, with their settings
              and folders, switched off until you choose what each does next.{" "}
              <strong>Remove them</strong> and they are gone; the folders and
              files they watch are not touched.
              {waiting
                ? " Neither is possible while a hand-off is in progress."
                : ""}
            </p>
          </>
        ) : null}
        {owed > 0 ? (
          <>
            <ul
              data-testid="media-manager-in-use-reports"
              className="list-disc pl-5"
            >
              {inUse.reports.map((report) => (
                <li key={report.handoff_id}>{report.name}</li>
              ))}
            </ul>
            <p>
              <strong>Give up</strong> and Weir stops waiting to tell {title}{" "}
              about {owed === 1 ? "this file" : "these files"}. Nothing is sent,
              and Activity records what was dropped.
            </p>
          </>
        ) : null}
      </div>
      <div className="mm-modal__actions">
        <button
          type="button"
          ref={keepRef}
          data-testid="media-manager-in-use-keep"
          className={mmActionButtonClass({ variant: "secondary" })}
          onClick={onKeep}
          disabled={busy}
        >
          Keep it
        </button>
        {owed > 0 ? (
          <button
            type="button"
            data-testid="media-manager-in-use-give-up-reports"
            className={mmActionButtonClass({ variant: "danger" })}
            onClick={onGiveUpReports}
            disabled={busy}
          >
            {busy
              ? "Working…"
              : `Give up on the ${owed === 1 ? "report" : `${owed} reports`} still owed to ${title}`}
          </button>
        ) : null}
        {hasWorkflows ? (
          <>
            <button
              type="button"
              data-testid="media-manager-in-use-unlink"
              className={mmActionButtonClass({ variant: "primary" })}
              onClick={onUnlink}
              disabled={busy || waiting}
            >
              {busy ? "Working…" : "Unlink the workflows"}
            </button>
            <button
              type="button"
              data-testid="media-manager-in-use-remove-workflows"
              className={mmActionButtonClass({ variant: "danger" })}
              onClick={onRemoveWorkflows}
              disabled={busy || waiting}
            >
              {busy ? "Working…" : "Remove the workflows"}
            </button>
          </>
        ) : null}
      </div>
    </ModalDialog>
  );
}
