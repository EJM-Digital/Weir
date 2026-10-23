import { useEffect, useRef, useState, type KeyboardEvent } from "react";

/** How long a run of typed characters keeps building the type-ahead match before it resets (#701). */
const TYPEAHEAD_RESET_MS = 750;

function clampIndex(index: number, lastIndex: number): number {
  return Math.min(Math.max(index, 0), lastIndex);
}

/** True for a single printable character with no modifier that would mean something else (e.g. Ctrl+C). */
function isTypeaheadKey(event: KeyboardEvent): boolean {
  return (
    event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey
  );
}

export interface ListboxKeyboardNav {
  /** The option the roving focus is currently on. */
  activeIndex: number;
  /** Focuses `index` and makes it the active option, e.g. on click or on open. */
  setActive: (index: number) => void;
  /** Stores each option's element so arrow/Home/End/type-ahead can move real DOM focus onto it. */
  registerOption: (
    index: number,
  ) => (element: HTMLButtonElement | null) => void;
  /** Attach to the element that hosts both the trigger and the panel. */
  onKeyDown: (event: KeyboardEvent) => void;
}

/**
 * Roving focus and type-ahead for an anchored listbox panel (#701): ArrowDown/ArrowUp move the active
 * option, Home/End jump to the ends, typing a letter jumps to the next option whose label starts with
 * what has been typed so far, and Enter/Space on a focused option activates it. Shared by
 * {@link MmListboxPicker} and {@link MmMultiListboxPicker} since both need the identical interaction.
 */
export function useListboxKeyboardNav<Option extends { label: string }>(
  options: readonly Option[],
  {
    isOpen,
    initialIndex = 0,
    onActivate,
  }: {
    isOpen: boolean;
    /** Which option the roving focus starts on the next time the panel opens. */
    initialIndex?: number;
    onActivate: (option: Option, index: number) => void;
  },
): ListboxKeyboardNav {
  const [activeIndex, setActiveIndex] = useState(initialIndex);
  const elements = useRef<(HTMLButtonElement | null)[]>([]);
  const typeahead = useRef("");
  const typeaheadResetTimer = useRef<ReturnType<typeof setTimeout> | undefined>(
    undefined,
  );
  // Kept in sync every render (in an effect, never during render itself) so the effect below can
  // honestly depend on `isOpen` alone: only the moment the panel opens should decide where roving
  // focus starts, never a later change to `initialIndex` or `options` while it is already open.
  const openStart = useRef({ initialIndex, lastIndex: options.length - 1 });
  useEffect(() => {
    openStart.current = { initialIndex, lastIndex: options.length - 1 };
  });

  useEffect(() => {
    if (!isOpen) return;
    const { initialIndex: start, lastIndex } = openStart.current;
    setActiveIndex(clampIndex(start, lastIndex));
  }, [isOpen]);

  useEffect(() => () => clearTimeout(typeaheadResetTimer.current), []);

  const moveActive = (index: number) => {
    const next = clampIndex(index, options.length - 1);
    setActiveIndex(next);
    elements.current[next]?.focus();
  };

  const jumpByTypeahead = (char: string) => {
    clearTimeout(typeaheadResetTimer.current);
    typeahead.current += char.toLowerCase();
    typeaheadResetTimer.current = setTimeout(() => {
      typeahead.current = "";
    }, TYPEAHEAD_RESET_MS);
    const match = options.findIndex((option) =>
      option.label.toLowerCase().startsWith(typeahead.current),
    );
    if (match >= 0) moveActive(match);
  };

  const onKeyDown = (event: KeyboardEvent) => {
    if (!isOpen || options.length === 0) return;
    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        moveActive(activeIndex + 1);
        return;
      case "ArrowUp":
        event.preventDefault();
        moveActive(activeIndex - 1);
        return;
      case "Home":
        event.preventDefault();
        moveActive(0);
        return;
      case "End":
        event.preventDefault();
        moveActive(options.length - 1);
        return;
      case "Enter":
      case " ": {
        // The trigger button already opens/closes itself on Enter/Space; only a focused
        // option (not the trigger) should be activated by them.
        const target = event.target as HTMLElement | null;
        if (target?.getAttribute("role") !== "option") return;
        event.preventDefault();
        onActivate(options[activeIndex], activeIndex);
        return;
      }
      default:
        if (isTypeaheadKey(event)) {
          event.preventDefault();
          jumpByTypeahead(event.key);
        }
    }
  };

  return {
    activeIndex,
    setActive: setActiveIndex,
    registerOption: (index) => (element) => {
      elements.current[index] = element;
    },
    onKeyDown,
  };
}
