import { useId, useState } from "react";

import { errorMessage } from "../../lib/api/error-message";
import { useConfirmRecoveryCodesMutation } from "../../lib/auth/queries";
import { RECOVERY_TEXT } from "../../lib/auth/recovery-text";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { saveBlobAs } from "../../lib/ui/save-file";
import { CopyLink } from "../ui/copy-link";

/** The codes as the downloaded text file holds them: a short note, then one code to a line. */
function codesFile(codes: readonly string[]): string {
  return [
    RECOVERY_TEXT.downloadHeader,
    RECOVERY_TEXT.downloadNote,
    "",
    ...codes,
    "",
  ].join("\n");
}

/**
 * Recovery codes, shown the one time Weir shows them: with Copy, Download as text and a check the person ticks to say they
 * saved them. Nothing continues until it is ticked and Weir has been told, so a set is never left unseen.
 */
export function SaveRecoveryCodes({
  codes,
  onSaved,
  finishLabel = RECOVERY_TEXT.continue,
}: {
  codes: readonly string[];
  onSaved: () => void;
  /** What the button that ends the screen says: Continue in setup, Done in System. */
  finishLabel?: string;
}) {
  const checkId = useId();
  const confirm = useConfirmRecoveryCodesMutation();
  const [saved, setSaved] = useState(false);

  const done = () => {
    confirm.mutate(undefined, { onSuccess: onSaved });
  };

  return (
    <div className="mm-recovery" data-testid="recovery-codes">
      <p className="mm-recovery__warning">{RECOVERY_TEXT.shownOnce}</p>
      <ol
        className="mm-recovery__codes"
        aria-label={RECOVERY_TEXT.codesListLabel}
        data-testid="recovery-codes-list"
      >
        {codes.map((code) => (
          <li key={code}>
            <code>{code}</code>
          </li>
        ))}
      </ol>
      <div className="mm-recovery__tools">
        <CopyLink value={codes.join("\n")} label={RECOVERY_TEXT.copyLabel} />
        <button
          type="button"
          className="mm-quiet-link"
          data-testid="recovery-codes-download"
          onClick={() =>
            saveBlobAs(
              new Blob([codesFile(codes)], { type: "text/plain" }),
              RECOVERY_TEXT.downloadFileName,
            )
          }
        >
          {RECOVERY_TEXT.download}
        </button>
      </div>
      <p className="mm-recovery__warning">{RECOVERY_TEXT.noWayBack}</p>
      <label className="mm-recovery__check" htmlFor={checkId}>
        <input
          id={checkId}
          type="checkbox"
          data-testid="recovery-codes-saved"
          checked={saved}
          disabled={confirm.isPending}
          onChange={(e) => setSaved(e.target.checked)}
        />
        <span>{RECOVERY_TEXT.savedCheck}</span>
      </label>
      {confirm.isError ? (
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {errorMessage(confirm.error, RECOVERY_TEXT.confirmFailed)}
        </p>
      ) : null}
      <button
        type="button"
        data-testid="recovery-codes-continue"
        className={mmActionButtonClass({ variant: "primary" })}
        disabled={!saved || confirm.isPending}
        onClick={done}
      >
        {confirm.isPending ? RECOVERY_TEXT.continueBusy : finishLabel}
      </button>
    </div>
  );
}
