import {
  useCallback,
  useEffect,
  useId,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from "react";

import { useCloseOnOutsideAndEscape } from "../../lib/ui/use-close-on-outside";
import { NavIconChevronDown } from "./nav-icons";

/** What each entry of the menu is, for finding them. */
const MENU_ITEM = "[role=menuitem]";

type MoreMenuProps<Id extends string> = {
  /** Names the menu for a screen reader. */
  menuLabel: string;
  /** What did not fit, in order: the words of each tab or chip folded into the menu. */
  folded: readonly Readonly<{ id: Id; label: ReactNode }>[];
  /** An entry was chosen from the menu. */
  onChoose: (id: Id) => void;
  /** Draws the button as the row's own tabs or chips are drawn. */
  buttonClassName: string;
  /** The button's words: "More" at the end of a row of tabs or chips. */
  label?: ReactNode;
  /** Which edge of the button the menu lines up with: a menu at a card's right edge opens towards its left. */
  align?: "start" | "end";
};

/** What the menu's arrow keys and Home and End do: the entry to focus next, or null for any other key. */
function entryAfterKey(
  key: string,
  index: number,
  count: number,
): number | null {
  switch (key) {
    case "ArrowDown":
      return (index + 1) % count;
    case "ArrowUp":
      return (index - 1 + count) % count;
    case "Home":
      return 0;
    case "End":
      return count - 1;
    default:
      return null;
  }
}

/**
 * "More ▾" at the end of a row of tabs or chips, opening a menu of the ones that did not fit, or a small menu of another
 * kind with its own words. It is a menu button, never drawn as a chosen one: the chosen tab or chip always has a place
 * in the row.
 */
export function MoreMenu<Id extends string>({
  menuLabel,
  folded,
  onChoose,
  buttonClassName,
  label = "More",
  align = "start",
}: MoreMenuProps<Id>) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const menuId = useId();
  const close = useCallback(() => setOpen(false), []);
  useCloseOnOutsideAndEscape(open, close, containerRef);

  useEffect(() => {
    if (open) menuRef.current?.querySelector<HTMLElement>(MENU_ITEM)?.focus();
  }, [open]);

  const closeAndReturn = () => {
    setOpen(false);
    buttonRef.current?.focus();
  };

  const onMenuKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === "Escape") {
      event.preventDefault();
      closeAndReturn();
      return;
    }
    if (event.key === "Tab") {
      setOpen(false);
      return;
    }
    const all = Array.from(
      event.currentTarget.querySelectorAll<HTMLElement>(MENU_ITEM),
    );
    const next = entryAfterKey(
      event.key,
      all.indexOf(document.activeElement as HTMLElement),
      all.length,
    );
    if (next === null) return;
    event.preventDefault();
    all[next]?.focus();
  };

  return (
    <div className="mm-more" ref={containerRef}>
      <button
        ref={buttonRef}
        type="button"
        className={`mm-more__button ${buttonClassName}`}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => setOpen((value) => !value)}
        onKeyDown={(event) => {
          if (event.key !== "ArrowDown" || open) return;
          event.preventDefault();
          setOpen(true);
        }}
      >
        {label}
        <NavIconChevronDown className="mm-more__chevron" />
      </button>
      {open ? (
        <div
          ref={menuRef}
          id={menuId}
          role="menu"
          tabIndex={-1}
          aria-label={menuLabel}
          className={`mm-more__menu${align === "end" ? " mm-more__menu--end" : ""}`}
          onKeyDown={onMenuKeyDown}
        >
          {folded.map(({ id, label }) => (
            <button
              key={id}
              type="button"
              role="menuitem"
              className="mm-more__item"
              onClick={() => {
                setOpen(false);
                onChoose(id);
              }}
            >
              {label}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
