/**
 * "Choose tracks by hand" (#501): an operator overrides the automatic rules for one held file by
 * picking exactly which video, audio and subtitle tracks to keep, their default and forced flags and
 * their order, then queues a remux pass built straight from that choice.
 */
import type {
  ProcessingFileTracks,
  ProcessingManualPlanChoice,
} from "../../lib/processing/files-api";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import {
  DroppedTracks,
  OtherStreams,
  TrackOrder,
  TrackTable,
} from "./choose-tracks-sections";
import { StoryPanelShell } from "./story-panel-shell";
import { useTrackChoice } from "./use-track-choice";

export interface ChooseTracksPanelProps {
  open: boolean;
  fileName: string;
  tracks: ProcessingFileTracks | undefined;
  loading: boolean;
  loadError: string | null;
  submitting: boolean;
  submitError: string | null;
  onClose: () => void;
  onSubmit: (choice: ProcessingManualPlanChoice) => void;
}

function FailedNote({ text, testId }: { text: string; testId?: string }) {
  return (
    <p
      className="mm-story-panel__note mm-status-text"
      data-status="broken"
      role="alert"
      data-testid={testId}
    >
      {text}
    </p>
  );
}

export function ChooseTracksPanel({
  open,
  fileName,
  tracks,
  loading,
  loadError,
  submitting,
  submitError,
  onClose,
  onSubmit,
}: ChooseTracksPanelProps): React.ReactElement | null {
  const choice = useTrackChoice(tracks);
  if (!open) return null;

  return (
    <StoryPanelShell
      eyebrow="Choose tracks by hand"
      title={fileName}
      backdropLabel="Close choose tracks"
      className="mm-tracks-panel"
      testId="choose-tracks-panel"
      onClose={onClose}
    >
      {loading ? (
        <p className="mm-story-panel__note">
          Reading a fresh probe of this file…
        </p>
      ) : loadError ? (
        <FailedNote text={loadError} />
      ) : !tracks ? null : (
        <>
          <p className="mm-story-panel__note">
            Weir just re-read this file&apos;s tracks. Choose what to keep, mark
            one default audio track and, if you want, one default subtitle
            track, then submit. Weir checks the file again right before
            processing it, and will ask you to choose again if it changed since
            now.
          </p>

          <TrackTable choice={choice} />
          <OtherStreams tracks={choice.other} />
          <TrackOrder choice={choice} />
          <DroppedTracks choice={choice} />

          {choice.validationError ? (
            <FailedNote text={choice.validationError} />
          ) : null}
          {submitError ? (
            <FailedNote
              text={submitError}
              testId="choose-tracks-submit-error"
            />
          ) : null}

          <div className="mm-tracks-footer">
            <button
              type="button"
              className={mmActionButtonClass({ variant: "tertiary" })}
              onClick={onClose}
              disabled={submitting}
            >
              Cancel
            </button>
            <button
              type="button"
              className={mmActionButtonClass({ variant: "primary" })}
              onClick={() => onSubmit(choice.choice())}
              disabled={submitting || Boolean(choice.validationError)}
              data-testid="choose-tracks-submit"
            >
              {submitting ? "Queueing…" : "Queue this choice"}
            </button>
          </div>
        </>
      )}
    </StoryPanelShell>
  );
}
