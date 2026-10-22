import { useCallback, useId, useRef, useState } from "react";
import {
  mmListboxOptionButtonClass,
  mmListboxPanelClass,
  mmPickerTriggerSurface,
} from "../../lib/ui/mm-control-roles";
import { useCloseOnOutsideAndEscape } from "../../lib/ui/use-close-on-outside";
import { MmPickerChevron } from "./mm-picker-chevron";
import { useListboxKeyboardNav } from "./use-listbox-keyboard-nav";

export type MmListboxOption = { value: string; label: string };

type MmListboxPickerProps = {
  options: readonly MmListboxOption[];
  value: string;
  onChange: (next: string) => void;
  disabled?: boolean;
  placeholder?: string;
  "data-testid"?: string;
  /** Use with a visible `<span id={…}>` label (same pattern as Global Settings timezone). */
  ariaLabelledBy?: string;
  ariaDescribedBy?: string;
  /** Optional width constraint on the anchor (e.g. `max-w-md`). */
  className?: string;
};

/**
 * Custom listbox picker — **visual and interaction parity** with Global Settings → Timezone
 * (anchored panel, option rows, click-outside, Escape).
 */
export function MmListboxPicker({
  options,
  value,
  onChange,
  disabled = false,
  placeholder = "Select…",
  "data-testid": dataTestId,
  ariaLabelledBy,
  ariaDescribedBy,
  className,
}: MmListboxPickerProps) {
  const autoId = useId();
  const listboxId = `${autoId}-listbox`;
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const close = useCallback(() => setOpen(false), []);
  useCloseOnOutsideAndEscape(open, close, containerRef);

  const selectedIndex = options.findIndex((o) => o.value === value);
  const nav = useListboxKeyboardNav(options, {
    isOpen: open,
    initialIndex: Math.max(selectedIndex, 0),
    onActivate: (option) => {
      onChange(option.value);
      setOpen(false);
      triggerRef.current?.focus();
    },
  });

  const selected = options.find((o) => o.value === value);
  const triggerLabel = selected?.label ?? placeholder;

  return (
    <div
      ref={containerRef}
      className={["relative", className].filter(Boolean).join(" ")}
    >
      <button
        ref={triggerRef}
        type="button"
        className={mmPickerTriggerSurface}
        disabled={disabled}
        aria-haspopup="listbox"
        aria-controls={open ? listboxId : undefined}
        aria-expanded={open}
        aria-labelledby={ariaLabelledBy}
        aria-describedby={ariaDescribedBy}
        data-testid={dataTestId}
        onClick={() => setOpen((v) => !v)}
        onKeyDown={nav.onKeyDown}
      >
        <span className="min-w-0 max-h-16 flex-1 overflow-y-auto whitespace-normal break-words text-left">
          {triggerLabel}
        </span>
        <MmPickerChevron open={open} />
      </button>
      {open ? (
        <div
          id={listboxId}
          className={mmListboxPanelClass}
          role="listbox"
          tabIndex={-1}
          onKeyDown={nav.onKeyDown}
        >
          {options.map((opt, index) => (
            <button
              key={opt.value === "" ? "__empty__" : opt.value}
              ref={nav.registerOption(index)}
              type="button"
              role="option"
              tabIndex={index === nav.activeIndex ? 0 : -1}
              aria-selected={opt.value === value}
              className={[
                mmListboxOptionButtonClass(opt.value === value),
                index === nav.activeIndex
                  ? "ring-1 ring-inset ring-mm-accent-ring"
                  : "",
              ]
                .filter(Boolean)
                .join(" ")}
              onMouseDown={(e) => {
                e.preventDefault();
                e.stopPropagation();
              }}
              onClick={(e) => {
                e.preventDefault();
                e.stopPropagation();
                nav.setActive(index);
                onChange(opt.value);
                setOpen(false);
                triggerRef.current?.focus();
              }}
            >
              {opt.label}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
