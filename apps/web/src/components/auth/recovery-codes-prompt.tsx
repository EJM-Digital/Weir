import { useRef, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";

import { useCanEdit } from "../../lib/auth/can-edit";
import {
  putRecoveryPromptAside,
  recoveryPromptPutAside,
} from "../../lib/auth/recovery-prompt";
import { needsRecoveryCodes } from "../../lib/auth/recovery-status";
import { RECOVERY_TEXT } from "../../lib/auth/recovery-text";
import { useRecoveryCodesQuery } from "../../lib/auth/queries";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { ModalDialog } from "../ui/modal-dialog";

/**
 * Asks, once after signing in, for recovery codes to be made when the account has no usable set (an account made before they
 * existed, or a set that was never marked as saved). "Not now" puts it aside until the next sign-in; System › Security keeps
 * asking until there is a set. Someone who only reads is not asked: the codes are for the account that can change things.
 */
export function RecoveryCodesPrompt() {
  const navigate = useNavigate();
  const location = useLocation();
  const canMake = useCanEdit();
  const status = useRecoveryCodesQuery(canMake);
  const [putAside, setPutAside] = useState(recoveryPromptPutAside);
  const makeRef = useRef<HTMLButtonElement>(null);

  const onSecurity =
    location.pathname === "/system" &&
    new URLSearchParams(location.search).get("tab") === "security";
  if (
    !canMake ||
    !status.data ||
    !needsRecoveryCodes(status.data) ||
    putAside ||
    onSecurity
  ) {
    return null;
  }

  const closeForNow = () => {
    putRecoveryPromptAside();
    setPutAside(true);
  };

  return (
    <ModalDialog
      title={RECOVERY_TEXT.promptHeading}
      testId="recovery-prompt"
      onClose={closeForNow}
      initialFocus={makeRef}
    >
      <div className="mm-modal__body space-y-2">
        <p>
          {status.data.remaining === 0
            ? RECOVERY_TEXT.promptLead
            : RECOVERY_TEXT.promptUnsavedLead}
        </p>
        <p>{RECOVERY_TEXT.noWayBack}</p>
      </div>
      <div className="mm-modal__actions">
        <button
          type="button"
          data-testid="recovery-prompt-later"
          className={mmActionButtonClass({ variant: "secondary" })}
          onClick={closeForNow}
        >
          {RECOVERY_TEXT.promptLater}
        </button>
        <button
          type="button"
          ref={makeRef}
          data-testid="recovery-prompt-make"
          className={mmActionButtonClass({ variant: "primary" })}
          onClick={() => {
            closeForNow();
            void navigate("/system?tab=security");
          }}
        >
          {RECOVERY_TEXT.promptMake}
        </button>
      </div>
    </ModalDialog>
  );
}
