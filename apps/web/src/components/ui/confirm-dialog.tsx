/**
 * The confirmation in front of an action that cannot be undone (#599). Focus lands on the cancel
 * button, or on `initialFocus` when the dialog first asks for a choice, so Enter on a freshly opened
 * dialog keeps things as they are; Escape cancels, except mid-request. `title` names the thing acted
 * on: "Remove this connection?" is useless with three on screen.
 */
import { useId, useRef, type ReactNode, type RefObject } from "react";

import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { ModalDialog } from "./modal-dialog";

export function ConfirmDialog({
  title,
  description,
  confirmLabel,
  cancelLabel = "Keep it",
  busy = false,
  busyLabel = "Removing…",
  confirmDisabled = false,
  initialFocus,
  tone,
  error = null,
  testId,
  onCancel,
  onConfirm,
}: {
  /** Names the item, e.g. "Remove Deluno?". */
  title: string;
  /** What confirming actually does, and whether it can be undone. */
  description?: ReactNode;
  confirmLabel: string;
  cancelLabel?: string;
  busy?: boolean;
  busyLabel?: string;
  /** True while the person still has to choose something before the action makes sense. */
  confirmDisabled?: boolean;
  /** A safe control to focus instead of the cancel button, such as the first choice being asked for. */
  initialFocus?: RefObject<HTMLElement | null>;
  /** `danger` for an action that removes, overrides or steps outside normal running: the confirming button is red and the cancel button stays neutral. Routine work stays `default`. */
  tone?: "default" | "danger";
  error?: string | null;
  testId: string;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const descriptionId = useId();
  const cancelRef = useRef<HTMLButtonElement>(null);

  return (
    <ModalDialog
      title={title}
      testId={testId}
      onClose={onCancel}
      busy={busy}
      describedBy={description ? descriptionId : undefined}
      initialFocus={initialFocus ?? cancelRef}
    >
      {description ? (
        <div id={descriptionId} className="mm-modal__body space-y-2">
          {description}
        </div>
      ) : null}
      {error ? (
        <p
          className="mm-modal__error mm-status-text"
          data-status="broken"
          role="alert"
        >
          {error}
        </p>
      ) : null}
      <div className="mm-modal__actions">
        <button
          type="button"
          ref={cancelRef}
          data-testid={`${testId}-cancel`}
          className={mmActionButtonClass({ variant: "secondary" })}
          onClick={onCancel}
          disabled={busy}
        >
          {cancelLabel}
        </button>
        <button
          type="button"
          data-testid={`${testId}-confirm`}
          className={mmActionButtonClass({
            variant: tone === "danger" ? "danger" : "primary",
          })}
          disabled={busy || confirmDisabled}
          onClick={onConfirm}
        >
          {busy ? busyLabel : confirmLabel}
        </button>
      </div>
    </ModalDialog>
  );
}
