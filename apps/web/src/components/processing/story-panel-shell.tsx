import { useId, type ReactNode } from "react";

import { useModalFocus } from "../../lib/ui/use-modal-focus";

/**
 * The slide-over the file panels share: a backdrop that closes it, a titled dialog, and focus moved
 * in and given back. Close it and the list behind is exactly where it was.
 */
export function StoryPanelShell({
  eyebrow,
  title,
  art,
  backdropLabel,
  className = "",
  testId,
  onClose,
  children,
}: {
  eyebrow: string;
  title: string;
  /** A picture at the start of the header, 2:3, such as the title's poster. */
  art?: ReactNode;
  /** What the backdrop button says it closes, for a screen reader. */
  backdropLabel: string;
  className?: string;
  testId?: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const titleId = useId();
  const panelRef = useModalFocus<HTMLDivElement>({ onClose });
  return (
    <div className="mm-story-layer">
      <button
        type="button"
        className="mm-story-backdrop"
        data-modal-companion=""
        aria-label={backdropLabel}
        onClick={onClose}
      />
      <div
        ref={panelRef}
        className={`mm-story-panel${className ? ` ${className}` : ""}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        data-testid={testId}
      >
        <header className="mm-story-panel__head">
          {art ? <div className="mm-story-panel__art">{art}</div> : null}
          <div className="mm-story-panel__titles">
            <p className="mm-story-panel__eyebrow">{eyebrow}</p>
            <h2 id={titleId} className="mm-story-panel__title" title={title}>
              {title}
            </h2>
          </div>
          <button
            type="button"
            className="mm-story-panel__close"
            onClick={onClose}
            aria-label="Close"
          >
            ×
          </button>
        </header>
        <div className="mm-story-panel__body">{children}</div>
      </div>
    </div>
  );
}
