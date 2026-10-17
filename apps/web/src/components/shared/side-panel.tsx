/**
 * A panel that slides over the page from the right, for editing or reading one thing without losing the page
 * behind it. Close it and you are exactly where you were. Anything that would otherwise open below the fold,
 * where clicking looks like nothing happened, belongs here. Escape closes it, focus moves into it, Tab stays
 * inside it and focus returns to whatever opened it, and the page behind it does not scroll.
 */
import type { ReactNode, RefObject } from "react";

import { useModalFocus } from "../../lib/ui/use-modal-focus";

export function SidePanel({
  open,
  title,
  eyebrow,
  subtitle,
  onClose,
  initialFocus,
  children,
  dataTestId,
}: {
  open: boolean;
  title: string;
  eyebrow?: string;
  /** One quiet line under the title: a path, a count, what this is. */
  subtitle?: string;
  onClose: () => void;
  /** The field to focus on open, for a panel that is a form. Without it the panel itself takes focus. */
  initialFocus?: RefObject<HTMLElement | null>;
  children: ReactNode;
  dataTestId?: string;
}): React.ReactElement | null {
  const panel = useModalFocus<HTMLElement>({
    open,
    onClose,
    lockScroll: true,
    initialFocus,
  });

  if (!open) return null;

  return (
    <>
      <button
        type="button"
        className="mm-drawer-backdrop"
        data-modal-companion=""
        aria-label="Close"
        onClick={onClose}
      />
      <section
        className="mm-drawer"
        role="dialog"
        aria-modal="true"
        aria-label={title}
        tabIndex={-1}
        ref={panel}
        data-testid={dataTestId}
      >
        <header className="mm-drawer__head">
          <div className="mm-drawer__titles">
            {eyebrow ? <p className="mm-drawer__eyebrow">{eyebrow}</p> : null}
            <h2 className="mm-drawer__title">{title}</h2>
            {subtitle ? <p className="mm-drawer__path">{subtitle}</p> : null}
          </div>
          <button
            type="button"
            className="mm-head-control mm-head-control--icon"
            aria-label="Close"
            onClick={onClose}
          >
            ×
          </button>
        </header>
        <div className="mm-drawer__body">{children}</div>
      </section>
    </>
  );
}
