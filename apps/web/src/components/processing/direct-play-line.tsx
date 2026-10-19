/**
 * The Direct Play badge (#467): which of the operator's devices will play a file without the
 * media server converting it, and why not. Information only, so nothing here offers to fix or
 * convert anything. Verdicts are carried in words, never by symbol or colour alone.
 */

import { Fragment } from "react";
import type {
  ProcessingDirectPlay,
  ProcessingDirectPlayVerdict,
} from "../../lib/processing/files-api";
import type { StatusMeaning } from "../../lib/ui/status-meaning";

const SYMBOL: Record<ProcessingDirectPlayVerdict, string> = {
  yes: "✓",
  no: "✗",
  maybe: "?",
  unknown: "",
};

/** What a device's verdict means: it plays the file, may not, cannot, or has not been measured. */
export const VERDICT_MEANING: Record<
  ProcessingDirectPlayVerdict,
  StatusMeaning
> = {
  yes: "done",
  maybe: "attention",
  no: "broken",
  unknown: "idle",
};

const WORD: Record<ProcessingDirectPlayVerdict, string> = {
  yes: "yes",
  no: "no",
  maybe: "maybe",
  unknown: "not measured yet",
};

const SENTENCE: Record<
  Exclude<ProcessingDirectPlayVerdict, "unknown">,
  string
> = {
  yes: "plays it directly",
  no: "cannot play it directly, so the media server will convert it",
  maybe: "may not play it directly, so the media server may convert it",
};

/** The first reason, shortened for the one-line view. "cannot play DTS audio" reads "DTS audio". */
function shortReason(entry: ProcessingDirectPlay): string | null {
  if (entry.verdict === "unknown") return WORD.unknown;
  const first = entry.reasons[0];
  if (!first) return null;
  const shown =
    entry.verdict === "no" ? first.replace(/^cannot play\s+/i, "") : first;
  const more = entry.reasons.length - 1;
  return more > 0 ? `${shown}, +${more} more` : shown;
}

function fullSentence(entry: ProcessingDirectPlay): string {
  if (entry.verdict === "unknown") {
    return `${entry.device_name}: not measured yet, so Weir cannot say.`;
  }
  const lead = `${entry.device_name} ${SENTENCE[entry.verdict]}`;
  return entry.reasons.length
    ? `${lead}: ${entry.reasons.join("; ")}.`
    : `${lead}.`;
}

function DeviceVerdict({
  entry,
}: {
  entry: ProcessingDirectPlay;
}): React.ReactElement {
  const symbol = SYMBOL[entry.verdict];
  const reason = shortReason(entry);
  return (
    <span
      data-status={VERDICT_MEANING[entry.verdict]}
      title={fullSentence(entry)}
    >
      {entry.device_name}
      {symbol ? (
        <>
          <span className="mm-status-text" aria-hidden="true">
            {" "}
            {symbol}
          </span>
          <span className="sr-only"> {WORD[entry.verdict]}</span>
        </>
      ) : null}
      {reason ? ` (${reason})` : null}
    </span>
  );
}

export function DirectPlayLine({
  directPlay,
  testId,
}: {
  directPlay: ProcessingDirectPlay[];
  testId?: string;
}): React.ReactElement | null {
  if (directPlay.length === 0) return null;

  const explained = directPlay.filter((entry) => entry.reasons.length > 0);
  return (
    <div className="mm-direct-play" data-testid={testId}>
      <span className="mm-direct-play__label">Direct Play:</span>{" "}
      {directPlay.map((entry, index) => (
        <Fragment key={entry.device_id}>
          {index > 0 ? <span aria-hidden="true"> · </span> : null}
          <DeviceVerdict entry={entry} />
        </Fragment>
      ))}
      {explained.length > 0 ? (
        <details className="mm-direct-play__why">
          <summary>Why</summary>
          <ul className="mm-direct-play__reasons">
            {explained.map((entry) => (
              <li key={entry.device_id}>{fullSentence(entry)}</li>
            ))}
          </ul>
        </details>
      ) : null}
    </div>
  );
}
