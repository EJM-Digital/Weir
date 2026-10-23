import { useState } from "react";
import { ChooseTracksPanel } from "../../components/processing/choose-tracks-panel";
import { ConfirmDialog } from "../../components/ui/confirm-dialog";
import { errorMessage } from "../../lib/api/error-message";
import {
  wasRemovedBelowMinimumSize,
  type ProcessingFile,
  type ProcessingFileStatus,
  type ProcessingFileTracks,
  type ProcessingManualPlanChoice,
} from "../../lib/processing/files-api";
import {
  useMoveProcessingFileToTop,
  useProcessProcessingFileNow,
  useProcessingCheckLibraryAgain,
  useProcessingFileTracks,
  useProcessingWhyHeld,
  useRequeueProcessingFile,
  useSubmitProcessingManualPlan,
} from "../../lib/processing/files-queries";
import { useProcessingLibrariesQuery } from "../../lib/processing/libraries-queries";
import { useHandedOffNote } from "../../lib/processing/use-handed-off-note";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { useFileRemoval } from "./use-file-removal";

const PASS_THROUGH_EXPLAINED =
  "Weir will skip the audio, subtitle and metadata rules, copy and check the original in this workflow's output folder, then keep or remove the watched original the way this workflow does after any finished file. Readiness checks still apply, so a download still being written is left alone.";

/** States Weir is done with a file in, one way or another: it can only be processed again from here. */
const CONCLUDED: readonly ProcessingFileStatus[] = [
  "processed",
  "passed_through",
  "rejected",
  "cancelled",
];

/**
 * Every action for the file open in Activity. Each shows only where it can do something: a running file cannot be
 * started earlier, and a button that looked like it worked would be worse than no button. Every action disables the
 * whole row while it runs and shows what it is doing, so nothing looks like it did nothing (#699).
 */
export function ActivityFileActions({
  file,
  editable,
  onRemoved,
}: {
  file: ProcessingFile;
  editable: boolean;
  /** Called instead of showing a local notice, since removing a file usually changes what is selected. */
  onRemoved: (message: string) => void;
}) {
  const libraries = useProcessingLibrariesQuery();
  const handedOff = useHandedOffNote(file.library_id);
  const moveToTop = useMoveProcessingFileToTop();
  const requeue = useRequeueProcessingFile();
  const whyHeld = useProcessingWhyHeld();
  const processNow = useProcessProcessingFileNow();
  const checkAgain = useProcessingCheckLibraryAgain();
  const fileTracks = useProcessingFileTracks();
  const submitManualPlan = useSubmitProcessingManualPlan();
  const [notice, setNotice] = useState<string | null>(null);
  const [tracksOpen, setTracksOpen] = useState(false);
  const [tracks, setTracks] = useState<ProcessingFileTracks | null>(null);
  const [tracksError, setTracksError] = useState<string | null>(null);
  const [planError, setPlanError] = useState<string | null>(null);
  const [confirmingPassThrough, setConfirmingPassThrough] = useState(false);
  const removal = useFileRemoval({
    fileId: file.id,
    fileName: file.relative_path,
    onRemoved: (message) => {
      onRemoved(message);
      setNotice(message);
    },
    onProblem: setNotice,
  });

  if (!editable) return null;

  const scope =
    libraries.data?.find((l) => l.id === file.library_id)?.media_type === "tv"
      ? "tv"
      : "movie";

  const anyPending =
    removal.checking ||
    removal.removing ||
    moveToTop.isPending ||
    requeue.isPending ||
    whyHeld.isPending ||
    processNow.isPending ||
    checkAgain.isPending;

  async function run(action: () => Promise<string>, failed: string) {
    setNotice(null);
    try {
      setNotice(await action());
    } catch (error) {
      setNotice(errorMessage(error, failed));
    }
  }

  async function openTracks() {
    setNotice(null);
    setTracksOpen(true);
    setTracks(null);
    setTracksError(null);
    setPlanError(null);
    try {
      setTracks(await fileTracks.mutateAsync(file.id));
    } catch (error) {
      setTracksError(
        errorMessage(
          error,
          "Weir could not read this file's tracks. Refresh and try again.",
        ),
      );
    }
  }

  async function submitTracks(choice: ProcessingManualPlanChoice) {
    setPlanError(null);
    try {
      await submitManualPlan.mutateAsync({ id: file.id, choice });
      setNotice(
        "Queued your track choice. Weir will check the file again before processing it.",
      );
      setTracksOpen(false);
    } catch (error) {
      setPlanError(
        errorMessage(
          error,
          "That track choice could not be queued. Refresh the tracks and try again.",
        ),
      );
    }
  }

  const status = file.status;
  // A file Weir has found gone, or one the workflow removed for being under its minimum size, has nothing left to choose tracks
  // for, check again, process or pass through: only forgetting it is left.
  const hasFile =
    file.source_gone !== true && !wasRemovedBelowMinimumSize(file);
  const queueAgain = () =>
    void run(
      async () => (await requeue.mutateAsync(file.id)).detail,
      "That file could not be queued again.",
    );

  const button = (
    label: string,
    title: string,
    onClick: () => void,
    options: { variant?: "secondary" | "tertiary" } & (
      | { pending?: undefined; pendingLabel?: undefined }
      | { pending: boolean; pendingLabel: string }
    ) = {},
  ) => (
    <button
      type="button"
      className={mmActionButtonClass({
        variant: options.variant ?? "secondary",
      })}
      title={title}
      disabled={anyPending}
      onClick={onClick}
    >
      {options.pending ? options.pendingLabel : label}
    </button>
  );

  return (
    <div className="mm-history-actions" data-testid="activity-file-actions">
      <div className="mm-history-actions__row">
        {status === "processing_failed"
          ? button(
              "Try again",
              "Tries this file again now, ignoring the automatic wait and attempt limit.",
              queueAgain,
              { pending: requeue.isPending, pendingLabel: "Queueing…" },
            )
          : null}
        {CONCLUDED.includes(status) && hasFile
          ? button(
              "Process again",
              "Queues this file to be processed again, from its original in the watched folder.",
              queueAgain,
              { pending: requeue.isPending, pendingLabel: "Queueing…" },
            )
          : null}
        {status === "unprocessed"
          ? button(
              "Process now",
              "Queues this file straight away.",
              () =>
                void run(async () => {
                  await processNow.mutateAsync({
                    relative_media_path: file.relative_path,
                    media_scope: scope,
                    library_id: file.library_id,
                  });
                  return "Queued this file. It starts as soon as Weir finishes what it is working on.";
                }, "That file could not be queued."),
              { pending: processNow.isPending, pendingLabel: "Queueing…" },
            )
          : null}
        {status === "unprocessed"
          ? button(
              "Move to top",
              "Puts this file ahead of everything else waiting.",
              () =>
                void run(
                  async () => (await moveToTop.mutateAsync(file.id)).detail,
                  "That file could not be moved to the front of the queue.",
                ),
              {
                variant: "tertiary",
                pending: moveToTop.isPending,
                pendingLabel: "Moving…",
              },
            )
          : null}
        {status === "on_hold" && hasFile
          ? button(
              "Choose tracks",
              "Reads this file's tracks again and lets you pick which to keep, instead of the saved rules.",
              () => void openTracks(),
            )
          : null}
        {/* Weir never scans a workflow a media manager hands its downloads to, so there is nothing to check again. */}
        {handedOff === null &&
        hasFile &&
        (status === "on_hold" ||
          status === "blocked_upstream" ||
          status === "skipped" ||
          status === "out_of_schedule")
          ? button(
              "Check again",
              "Checks this workflow now and queues files that are ready.",
              () =>
                void run(async () => {
                  await checkAgain.mutateAsync({
                    media_scope: scope,
                    library_id: file.library_id,
                  });
                  return "Weir is checking this workflow again and will queue the file when it is ready.";
                }, "That workflow could not be checked again. Review its watched folder and try again."),
              { pending: checkAgain.isPending, pendingLabel: "Checking…" },
            )
          : null}
        {(status === "blocked_upstream" || status === "on_hold") && hasFile
          ? button(
              "Why is this held?",
              "Asks every media manager covering this workflow what it is doing with this file, right now.",
              () =>
                void run(async () => {
                  const answer = await whyHeld.mutateAsync(file.id);
                  return answer.reasons.length
                    ? answer.reasons.join(" ")
                    : "The media managers had nothing to say about this file.";
                }, "Weir could not ask why that file is held."),
              {
                variant: "tertiary",
                pending: whyHeld.isPending,
                pendingLabel: "Asking…",
              },
            )
          : null}
        {status !== "processing" &&
        status !== "processed" &&
        status !== "disabled" &&
        hasFile
          ? button(
              "Pass through unchanged",
              "Skips your track rules, places a checked, unchanged copy in the output folder, then tidies the original as after any finished file.",
              () => setConfirmingPassThrough(true),
              { variant: "tertiary" },
            )
          : null}
        {status !== "processing"
          ? button(
              "Remove from list",
              "Forgets Weir's record of this file. Asks what to do first when its download is still in the watched folder.",
              () => {
                setNotice(null);
                void removal.start();
              },
              {
                variant: "tertiary",
                pending: removal.checking || removal.removing,
                pendingLabel: removal.checking ? "Checking…" : "Removing…",
              },
            )
          : null}
      </div>
      {confirmingPassThrough ? (
        <ConfirmDialog
          testId="activity-pass-through-confirm"
          title="Pass this file through unchanged?"
          description={PASS_THROUGH_EXPLAINED}
          confirmLabel="Pass through unchanged"
          cancelLabel="Not now"
          onCancel={() => setConfirmingPassThrough(false)}
          onConfirm={() => {
            setConfirmingPassThrough(false);
            void run(async () => {
              await processNow.mutateAsync({
                relative_media_path: file.relative_path,
                media_scope: scope,
                library_id: file.library_id,
                pass_through_unchanged: true,
              });
              return "Queued to pass through unchanged. Weir checks the copy before removing the original.";
            }, "That file could not be queued to pass through. Refresh and review its workflow's folders.");
          }}
        />
      ) : null}
      {removal.dialog}
      {notice ? (
        <p className="mm-history-note" role="status">
          {notice}
        </p>
      ) : null}
      <ChooseTracksPanel
        open={tracksOpen}
        fileName={file.relative_path}
        tracks={tracks ?? undefined}
        loading={fileTracks.isPending}
        loadError={tracksError}
        submitting={submitManualPlan.isPending}
        submitError={planError}
        onClose={() => setTracksOpen(false)}
        onSubmit={(choice) => void submitTracks(choice)}
      />
    </div>
  );
}
