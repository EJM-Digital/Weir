import { useCallback, useId, useRef, useState, type ReactNode } from "react";
import {
  mmCheckboxControlClass,
  mmListboxPanelClass,
  mmPickerTriggerSurface,
} from "../../lib/ui/mm-control-roles";
import { useCloseOnOutsideAndEscape } from "../../lib/ui/use-close-on-outside";
import type { MmListboxOption } from "./mm-listbox-picker";
import { useListboxKeyboardNav } from "./use-listbox-keyboard-nav";

/** An option, optionally with a small mark (a coloured dot, say) drawn before its label. */
export type MmMultiListboxOption = MmListboxOption & { marker?: ReactNode };

type MmMultiListboxPickerProps = {
  options: readonly MmMultiListboxOption[];
  values: readonly string[];
  onChange: (next: string[]) => void;
  disabled?: boolean;
  placeholder?: string;
  /** When set, shown on the closed trigger instead of joined option labels or the placeholder. */
  summaryText?: string;
  "data-testid"?: string;
  ariaLabelledBy?: string;
  ariaDescribedBy?: string;
  className?: string;
};

export function MmMultiListboxPicker({
  options,
  values,
  onChange,
  disabled = false,
  placeholder = "Select one or more…",
  summaryText,
  "data-testid": dataTestId,
  ariaLabelledBy,
  ariaDescribedBy,
  className,
}: MmMultiListboxPickerProps) {
  const autoId = useId();
  const listboxId = `${autoId}-listbox`;
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const close = useCallback(() => setOpen(false), []);
  useCloseOnOutsideAndEscape(open, close, containerRef);

  const selectedSet = new Set(values);
  const firstSelectedIndex = options.findIndex((o) => selectedSet.has(o.value));
  const nav = useListboxKeyboardNav(options, {
    isOpen: open,
    initialIndex: Math.max(firstSelectedIndex, 0),
    onActivate: (option) => {
      const next = selectedSet.has(option.value)
        ? values.filter((v) => v !== option.value)
        : [...values, option.value];
      onChange(next);
    },
  });

  const selectedLabels = options
    .filter((o) => selectedSet.has(o.value))
    .map((o) => o.label);
  const triggerLabel =
    summaryText !== undefined
      ? summaryText
      : selectedLabels.length > 0
        ? selectedLabels.join(", ")
        : placeholder;

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
        <svg
          aria-hidden
          className={[
            // The same accent mark a native select draws, so an anchored listbox and a select read alike.
            "h-4 w-4 shrink-0 text-mm-accent transition-transform",
            open ? "rotate-180" : "",
          ].join(" ")}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path d="M5.5 7.5 10 12l4.5-4.5H5.5z" />
        </svg>
      </button>
      {open ? (
        <div
          id={listboxId}
          className={mmListboxPanelClass}
          role="listbox"
          aria-multiselectable="true"
          tabIndex={-1}
          onKeyDown={nav.onKeyDown}
        >
          {options.map((opt, index) => {
            const checked = selectedSet.has(opt.value);
            const active = index === nav.activeIndex;
            return (
              <button
                key={opt.value}
                ref={nav.registerOption(index)}
                type="button"
                role="option"
                tabIndex={active ? 0 : -1}
                aria-selected={checked}
                className={[
                  "flex w-full items-start gap-3 px-3 py-2 text-left text-sm transition-colors",
                  checked
                    ? "bg-mm-accent-soft/25 text-mm-text1"
                    : "text-mm-text2 hover:bg-mm-accent-soft hover:text-mm-text1",
                  active ? "ring-1 ring-inset ring-mm-accent-ring" : "",
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
                  const next = checked
                    ? values.filter((v) => v !== opt.value)
                    : [...values, opt.value];
                  onChange(next);
                }}
              >
                <input
                  type="checkbox"
                  readOnly
                  checked={checked}
                  className={mmCheckboxControlClass}
                  tabIndex={-1}
                />
                {opt.marker ? (
                  <span className="inline-flex items-center gap-2">
                    {opt.marker}
                    {opt.label}
                  </span>
                ) : (
                  <span>{opt.label}</span>
                )}
              </button>
            );
          })}
        </div>
      ) : null}
    </div>
  );
}
