/**
 * What happened to one file, told in plain language (#468).
 *
 * The story itself is written by the backend from the stored pass
 * record, so this component only presents it — the raw record stays one click away for anyone who
 * wants the technical detail, but it is never the first thing shown.
 */

import { Poster } from "../shared/poster";
import { STORY_STEP_MEANING } from "../../lib/processing/story-step-meaning";
import { fileActivityRetentionNote } from "../../lib/processing/file-activity-retention";
import { useAppDateFormatter } from "../../lib/ui/mm-format-date";
import { FileNowSection, type FileNow } from "./file-now-section";
import { StoryPanelShell } from "./story-panel-shell";
import type {
  ProcessingFileLog,
  ProcessingFileStoryStep,
} from "../../lib/processing/files-api";

function Step({ step }: { step: ProcessingFileStoryStep }): React.ReactElement {
  return (
    <li className="mm-story-step" data-status={STORY_STEP_MEANING[step.tone]}>
      <p className="mm-story-step__heading">{step.heading}</p>
      <p className="mm-story-step__sentence">{step.sentence}</p>
    </li>
  );
}

export interface FileStoryPanelProps {
  open: boolean;
  fileName: string;
  /** What the header shows beside the name: the title's poster, tinted for its workflow when there is none. */
  poster?: { url: string | null | undefined; workflow: string };
  /** Where the file is on the Pipeline right now, when it is on it: shown above what has already happened to it. */
  now?: FileNow;
  log: ProcessingFileLog | undefined;
  loading: boolean;
  error: string | null;
  onClose: () => void;
}

export function FileStoryPanel({
  open,
  fileName,
  poster,
  now,
  log,
  loading,
  error,
  onClose,
}: FileStoryPanelProps): React.ReactElement | null {
  const formatWhen = useAppDateFormatter();

  if (!open) return null;

  const retention = log ? fileActivityRetentionNote(log.retention_days) : null;

  return (
    <StoryPanelShell
      eyebrow="What happened to this file"
      title={fileName}
      art={
        poster ? (
          <Poster
            url={poster.url}
            title={fileName}
            workflow={poster.workflow}
          />
        ) : undefined
      }
      backdropLabel="Close file activity"
      onClose={onClose}
    >
      {now ? <FileNowSection now={now} /> : null}
      {loading ? (
        <p className="mm-story-panel__note">Loading…</p>
      ) : error ? (
        <p
          className="mm-story-panel__note mm-status-text"
          data-status="broken"
          role="alert"
        >
          {error}
        </p>
      ) : !log || log.entries.length === 0 ? (
        <p className="mm-story-panel__note">
          {now
            ? "What Weir kept and removed shows here once it has finished."
            : "Nothing yet. Weir hasn't worked on this file."}
        </p>
      ) : (
        log.entries.map((entry) => (
          <section key={entry.id} className="mm-story-pass">
            <h3 className="mm-story-pass__when">
              {formatWhen(entry.recorded_at)}
            </h3>
            {entry.story.length > 0 ? (
              <ol className="mm-story-steps">
                {entry.story.map((step, index) => (
                  <Step key={`${entry.id}-${index}`} step={step} />
                ))}
              </ol>
            ) : (
              <p className="mm-story-panel__note">{entry.title}</p>
            )}
            <details className="mm-story-pass__detail">
              <summary>Technical detail</summary>
              <pre>{JSON.stringify(entry.detail, null, 2)}</pre>
            </details>
          </section>
        ))
      )}
      {retention ? (
        <p className="mm-story-panel__retention">{retention}</p>
      ) : null}
    </StoryPanelShell>
  );
}
