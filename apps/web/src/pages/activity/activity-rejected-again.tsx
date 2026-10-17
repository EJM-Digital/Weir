import { useState } from "react";

import { ConfirmDialog } from "../../components/ui/confirm-dialog";
import { errorMessage } from "../../lib/api/error-message";
import type { RejectedFilesSummary } from "../../lib/processing/rejected-files-api";
import {
  useProcessRejectedFilesAgain,
  useRejectedFilesSummary,
} from "../../lib/processing/rejected-files-queries";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { plural } from "../../lib/ui/mm-plural";

/**
 * "Process all again": after the rules change, every rejected file whose original is still in its watched folder goes
 * back to work in one step. It covers the whole rejected set (of the workflow Activity is narrowed to, if any), not just
 * the rows listed for the period on screen, and the dialog says so along with how many files that is.
 *
 * What it did is said beside the button. A page whose list drops the button with the last rejected file (the Dashboard's Needs
 * you) takes the words with `onNotice` instead, so they outlast the button.
 */
export function ProcessRejectedAgain({
  libraryId,
  libraryName,
  onNotice,
}: {
  libraryId: number | undefined;
  libraryName: string | undefined;
  onNotice?: (text: string, failed: boolean) => void;
}) {
  const count = useRejectedFilesSummary();
  const processAgain = useProcessRejectedFilesAgain();
  const [notice, setNotice] = useState<string | null>(null);
  const [asking, setAsking] = useState<RejectedFilesSummary | null>(null);
  const say = (text: string, failed = false) => {
    if (onNotice) onNotice(text, failed);
    else setNotice(text);
  };
  const scope = libraryName ? `the “${libraryName}” workflow` : "all workflows";

  function ask() {
    setNotice(null);
    count.mutate(libraryId, {
      onSuccess: (summary) => {
        if (summary.ready > 0) {
          processAgain.reset();
          setAsking(summary);
        } else {
          say(nothingToProcess(summary));
        }
      },
      onError: (error) =>
        say(errorMessage(error, "Couldn't count the rejected files."), true),
    });
  }

  function confirm() {
    processAgain.mutate(libraryId, {
      onSuccess: (result) => {
        setAsking(null);
        say(
          result.requeued > 0
            ? queuedNotice(result.requeued, result.skipped)
            : result.detail,
        );
      },
    });
  }

  return (
    <div className="mm-history-bulk" data-testid="activity-process-rejected">
      <button
        type="button"
        className={mmActionButtonClass({ variant: "secondary" })}
        title="Checks every rejected file again with your current rules."
        disabled={count.isPending}
        onClick={ask}
      >
        {count.isPending ? "Checking…" : "Process all again"}
      </button>
      {notice ? (
        <span className="mm-history-note" role="status">
          {notice}
        </span>
      ) : null}
      {asking ? (
        <ConfirmDialog
          testId="activity-process-rejected-confirm"
          title={`Process ${plural(asking.ready, "rejected file", "rejected files")} again with your current rules?`}
          description={
            <>
              <p>
                This covers every rejected file in {scope}, not only the ones
                Activity is listing now. Anything your rules still turn down is
                rejected again.
              </p>
              {asking.rejected > asking.ready ? (
                <p>{skippedSentence(asking.rejected - asking.ready)}</p>
              ) : null}
            </>
          }
          confirmLabel="Process all again"
          cancelLabel="Not now"
          busy={processAgain.isPending}
          busyLabel="Queueing…"
          error={
            processAgain.isError
              ? errorMessage(
                  processAgain.error,
                  "Weir could not queue those files. Try again.",
                )
              : null
          }
          onCancel={() => setAsking(null)}
          onConfirm={confirm}
        />
      ) : null}
    </div>
  );
}

/** What a bulk queue did, counted: "Queued 3 files.", or "Queued 3 files · 1 skipped." */
function queuedNotice(queued: number, skipped: number): string {
  const done = `Queued ${plural(queued, "file", "files")}`;
  return skipped > 0 ? `${done} · ${skipped} skipped.` : `${done}.`;
}

/** What happens to the rejected files whose original is gone. */
function skippedSentence(gone: number): string {
  return `${plural(gone, "other rejected file is", "other rejected files are")} skipped, because ${gone === 1 ? "its original is" : "their originals are"} no longer in the watched folder.`;
}

/** Why the button did nothing: there is nothing rejected, or every original is gone. */
function nothingToProcess(summary: RejectedFilesSummary): string {
  if (summary.rejected === 0) return "No rejected files.";
  return summary.rejected === 1
    ? "Original gone · can't process again."
    : "Originals gone · can't process again.";
}
