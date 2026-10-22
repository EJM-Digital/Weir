import { Chip } from "../../../../components/panels/chip";
import { useUpdateStateQuery } from "../../../../lib/settings/queries";
import type { UpdateStatus } from "../../../../lib/settings/types";
import { updateMeaning } from "../../../../lib/settings/update-status";
import { useAppDateFormatter } from "../../../../lib/ui/mm-format-date";
import { updateStatusLabel } from "./update-words";

/** The line beside the status pill, as a sentence: the new version and the one running, or the one running, else the server's own summary. */
function statusText(status: UpdateStatus): string {
  if (status.status === "update_available" && status.latest_version)
    return `Weir v${status.latest_version} is available (you have v${status.current_version})`;
  if (status.status === "up_to_date")
    return `You have Weir v${status.current_version}`;
  return status.summary;
}

function StatusSentence({
  status,
  shown,
  text,
}: {
  status: UpdateStatus;
  /** The status the pill shows, which is the server's own unless a check made by hand has answered. */
  shown: string;
  text: string;
}) {
  return (
    <p
      className="mm-status-line"
      title={status.summary}
      data-testid="suite-settings-release-status"
    >
      <Chip meaning={updateMeaning(shown)}>{updateStatusLabel(shown)}</Chip>
      <span>{text}</span>
    </p>
  );
}

/**
 * While the Windows app is not checking by itself, a check the person asks for still happens, in the tray, and its answer is
 * shown here: the version found or that nothing is newer, and when it was made, with a reminder that Weir will not look again
 * unless asked. Before any check has been made it says how to look once.
 */
function CheckingOffStatus({ status }: { status: UpdateStatus }) {
  const state = useUpdateStateQuery(true).data;
  const formatDate = useAppDateFormatter();
  if (!state?.last_checked_at) {
    return (
      <StatusSentence
        status={status}
        shown={status.status}
        text={statusText(status)}
      />
    );
  }

  const found = state.pending_version;
  const result = found ? "update_available" : "up_to_date";
  const answer = found
    ? `Weir v${found} is available (you have v${status.current_version})`
    : `You have Weir v${status.current_version}`;
  return (
    <>
      <StatusSentence status={status} shown={result} text={answer} />
      <p className="mm-quiet-note" data-testid="suite-settings-manual-check">
        Checked {formatDate(state.last_checked_at)}. Update checking is off, so
        Weir will not look again by itself. Press Check now to look once, or
        turn it on below.
      </p>
    </>
  );
}

/** What the Updates card says about the install and the newest release. */
export function UpdateStatusLine({
  status,
  isWindows,
}: {
  status: UpdateStatus;
  isWindows: boolean;
}) {
  if (isWindows && status.status === "off") {
    return <CheckingOffStatus status={status} />;
  }
  return (
    <StatusSentence
      status={status}
      shown={status.status}
      text={statusText(status)}
    />
  );
}
