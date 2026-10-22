/**
 * The workflow picker, on the header's title line after "Library", drawn as the Dashboard's "All workflows" picker
 * is: the same bordered field, height, type and width. One library shows its name in that box with no control;
 * several open a list from it, with a search box once there are enough to need one. The list works from the keyboard
 * like any list box: arrows, Home and End move, Enter picks, Escape closes.
 */
import { useEffect, useRef, useState } from "react";
import { MmPickerChevron } from "../../components/ui/mm-picker-chevron";
import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import { mmPickerTriggerSurface } from "../../lib/ui/mm-control-roles";

const SEARCH_FROM = 7;

/** The key that moves focus, and where to: one step, or to either end. */
const MOVES: Record<string, (at: number, count: number) => number> = {
  ArrowDown: (at, count) => (at + 1) % count,
  ArrowUp: (at, count) => (at - 1 + count) % count,
  Home: () => 0,
  End: (_at, count) => count - 1,
};

function options(menu: HTMLElement): HTMLElement[] {
  return [...menu.querySelectorAll<HTMLElement>('[role="option"]')];
}

export function LibraryPicker({
  libraries,
  chosenId,
  onPick,
  countFor,
}: {
  libraries: ProcessingLibrary[];
  chosenId: number;
  onPick: (id: number) => void;
  /** Files in a library, when that is known without asking the server for it. */
  countFor?: (id: number) => number | null;
}): React.ReactElement {
  const [open, setOpen] = useState(false);
  const [find, setFind] = useState("");
  const wrap = useRef<HTMLDivElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const chosen = libraries.find((library) => library.id === chosenId);
  const searchable = libraries.length >= SEARCH_FROM;

  useEffect(() => {
    if (!open) return undefined;
    // Opening lands on the search box when there is one, otherwise on the library already chosen.
    const root = menu.current;
    const start = searchable
      ? root?.querySelector<HTMLElement>("input")
      : (root?.querySelector<HTMLElement>(
          '[role="option"][aria-selected="true"]',
        ) ?? root?.querySelector<HTMLElement>('[role="option"]'));
    start?.focus();

    const away = (event: MouseEvent) => {
      if (!wrap.current?.contains(event.target as Node)) setOpen(false);
    };
    const key = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      setOpen(false);
      trigger.current?.focus();
    };
    document.addEventListener("mousedown", away);
    document.addEventListener("keydown", key);
    return () => {
      document.removeEventListener("mousedown", away);
      document.removeEventListener("keydown", key);
    };
  }, [open, searchable]);

  if (libraries.length <= 1) {
    return (
      <span
        className="mm-workflow-picker mm-library-picker mm-library-picker--one"
        data-testid="library-picker"
        title={chosen?.name}
      >
        {chosen?.name ?? "—"}
      </span>
    );
  }

  const shown = find.trim()
    ? libraries.filter((library) =>
        library.name.toLowerCase().includes(find.trim().toLowerCase()),
      )
    : libraries;

  const moveFocus = (event: React.KeyboardEvent<HTMLElement>) => {
    const move = MOVES[event.key];
    const list = options(event.currentTarget);
    if (!move || list.length === 0) return;
    // Home and End still move the caret while typing in the search box.
    const inSearch = event.target instanceof HTMLInputElement;
    if (inSearch && (event.key === "Home" || event.key === "End")) return;
    event.preventDefault();
    const at = list.indexOf(document.activeElement as HTMLElement);
    // From the search box, down goes to the first library and up to the last.
    const next =
      at < 0
        ? event.key === "ArrowUp"
          ? list.length - 1
          : 0
        : move(at, list.length);
    list[next]?.focus();
  };

  return (
    <div
      className="mm-workflow-picker mm-library-picker"
      ref={wrap}
      data-testid="library-picker"
    >
      <button
        type="button"
        ref={trigger}
        className={mmPickerTriggerSurface}
        aria-haspopup="listbox"
        aria-expanded={open}
        title={chosen?.name}
        onClick={() => setOpen((value) => !value)}
      >
        <span className="min-w-0 flex-1 truncate text-left">
          {chosen?.name ?? "Choose"}
        </span>
        <MmPickerChevron open={open} />
      </button>
      {open ? (
        <div
          className="mm-library-menu"
          role="listbox"
          aria-label="Choose a workflow"
          ref={menu}
          tabIndex={-1}
          onKeyDown={moveFocus}
        >
          {searchable ? (
            <input
              type="search"
              className="mm-input"
              aria-label="Find a workflow"
              placeholder="Find a workflow"
              value={find}
              onChange={(event) => setFind(event.target.value)}
            />
          ) : null}
          {shown.map((library) => {
            const count = countFor?.(library.id) ?? null;
            return (
              <button
                key={library.id}
                type="button"
                role="option"
                aria-selected={library.id === chosenId}
                tabIndex={-1}
                className={`mm-library-menu__item${library.id === chosenId ? " mm-library-menu__item--on" : ""}`}
                onClick={() => {
                  onPick(library.id);
                  setOpen(false);
                  trigger.current?.focus();
                }}
              >
                <span>{library.name}</span>
                <small>
                  {library.media_type === "tv" ? "TV episodes" : "Movies"}
                  {count !== null ? ` · ${count.toLocaleString()} files` : ""}
                </small>
              </button>
            );
          })}
          {shown.length === 0 ? (
            <p className="mm-library-menu__empty">No workflow by that name.</p>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
