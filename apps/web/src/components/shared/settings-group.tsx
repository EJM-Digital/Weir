import type { ReactNode } from "react";

import { Panel } from "../panels/panel";
import { quietActionRowClass } from "./quiet-section";

/**
 * One group of settings: a panel with the group's name in its header, a line on why it matters, then one
 * setting per row, hairlines between. `aside` sits at the header's right edge; `footer` holds the group's own
 * actions under a hairline.
 */
export function SettingsGroup({
  title,
  detail,
  aside,
  footer,
  children,
  testId,
}: {
  title: string;
  detail?: ReactNode;
  aside?: ReactNode;
  footer?: ReactNode;
  children: ReactNode;
  testId?: string;
}) {
  return (
    <Panel title={title} padded aside={aside} dataTestId={testId}>
      {detail ? <p className="mm-setgroup__detail">{detail}</p> : null}
      <div className="mm-setgroup__rows">{children}</div>
      {footer ? <div className={quietActionRowClass}>{footer}</div> : null}
    </Panel>
  );
}

/**
 * One setting: its name and a line on what it does, and the control at the end of the row. `htmlFor` ties the name
 * to a single input; a control that labels itself (a switch, a group of buttons) leaves it out.
 */
export function SettingRow({
  label,
  hint,
  htmlFor,
  children,
  stacked = false,
}: {
  label: ReactNode;
  hint?: ReactNode;
  htmlFor?: string;
  children: ReactNode;
  /** The control goes under the name instead of beside it, for one too wide to share the row. */
  stacked?: boolean;
}) {
  return (
    <div className={`mm-setrow${stacked ? " mm-setrow--stacked" : ""}`}>
      <div className="mm-setrow__text">
        {htmlFor ? (
          <label className="mm-setrow__label" htmlFor={htmlFor}>
            {label}
          </label>
        ) : (
          <span className="mm-setrow__label">{label}</span>
        )}
        {hint ? <span className="mm-setrow__hint">{hint}</span> : null}
      </div>
      <div className="mm-setrow__control">{children}</div>
    </div>
  );
}

/** A number with its unit after it, in the short field width every number uses. */
export function NumberWithUnit({
  id,
  value,
  unit,
  min,
  max,
  step,
  disabled,
  onChange,
}: {
  id: string;
  value: string;
  unit: string;
  min?: number;
  max?: number;
  step?: number;
  disabled?: boolean;
  onChange: (next: string) => void;
}) {
  return (
    <span className="mm-setrow__unit">
      <input
        id={id}
        className="mm-input mm-setrow__number"
        type="number"
        inputMode="decimal"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value)}
      />
      <span>{unit}</span>
    </span>
  );
}
