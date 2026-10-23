import { QuietFieldGroup } from "../../../../components/shared/quiet-section";
import {
  mmActionButtonClass,
  mmCheckboxControlClass,
} from "../../../../lib/ui/mm-control-roles";
import {
  SORTER_FIELDS,
  SORTER_LABELS,
  type TrackSorter,
} from "./rule-set-model";

const NEW_CRITERION: TrackSorter = {
  field: "language",
  value: "",
  reversed: false,
};

function CriterionRow({
  row,
  position,
  count,
  groupTitle,
  disabled,
  onChange,
  onMove,
  onRemove,
}: {
  row: TrackSorter;
  position: number;
  count: number;
  groupTitle: string;
  disabled: boolean;
  onChange: (row: TrackSorter) => void;
  onMove: (offset: -1 | 1) => void;
  onRemove: () => void;
}) {
  const tertiary = mmActionButtonClass({ variant: "tertiary" });
  return (
    <li className="mm-sorter-row">
      <span className="mm-sorter-row__number">{position + 1}</span>
      <label className="mm-sorter-row__field">
        Criterion
        <select
          className="mm-input"
          value={row.field}
          disabled={disabled}
          onChange={(event) => onChange({ ...row, field: event.target.value })}
        >
          {SORTER_FIELDS.map((field) => (
            <option key={field} value={field}>
              {SORTER_LABELS[field]}
            </option>
          ))}
        </select>
      </label>
      <label className="mm-sorter-row__field">
        Match value (optional)
        <input
          className="mm-input"
          value={row.value}
          placeholder="eng, >=5.1, dts, commentary…"
          disabled={disabled}
          onChange={(event) => onChange({ ...row, value: event.target.value })}
        />
      </label>
      <div className="flex flex-wrap items-end gap-1">
        <label className="mm-sorter-row__reverse">
          <input
            type="checkbox"
            className={mmCheckboxControlClass}
            checked={row.reversed}
            disabled={disabled}
            onChange={(event) =>
              onChange({ ...row, reversed: event.target.checked })
            }
          />
          Reverse
        </label>
        <button
          type="button"
          className={tertiary}
          disabled={disabled || position === 0}
          onClick={() => onMove(-1)}
          aria-label={`Move ${groupTitle} criterion ${position + 1} up`}
        >
          ↑
        </button>
        <button
          type="button"
          className={tertiary}
          disabled={disabled || position === count - 1}
          onClick={() => onMove(1)}
          aria-label={`Move ${groupTitle} criterion ${position + 1} down`}
        >
          ↓
        </button>
        <button
          type="button"
          className={tertiary}
          disabled={disabled}
          onClick={onRemove}
        >
          Remove
        </button>
      </div>
    </li>
  );
}

/** An ordered list of criteria that ranks tracks; the first one that tells two tracks apart wins. */
export function SorterEditor({
  title,
  detail,
  rows,
  disabled,
  onChange,
}: {
  title: string;
  detail: string;
  rows: TrackSorter[];
  disabled: boolean;
  onChange: (rows: TrackSorter[]) => void;
}) {
  const move = (index: number, offset: -1 | 1) => {
    const target = index + offset;
    const next = [...rows];
    [next[index], next[target]] = [next[target], next[index]];
    onChange(next);
  };

  return (
    <QuietFieldGroup title={title} detail={detail}>
      <ol>
        {rows.map((row, index) => (
          <CriterionRow
            key={index}
            row={row}
            position={index}
            count={rows.length}
            groupTitle={title}
            disabled={disabled}
            onChange={(next) =>
              onChange(rows.map((item, i) => (i === index ? next : item)))
            }
            onMove={(offset) => move(index, offset)}
            onRemove={() => onChange(rows.filter((_, i) => i !== index))}
          />
        ))}
      </ol>
      <div className="mt-4">
        <button
          type="button"
          className={mmActionButtonClass({ variant: "secondary" })}
          disabled={disabled}
          onClick={() => onChange([...rows, { ...NEW_CRITERION }])}
        >
          Add criterion
        </button>
      </div>
    </QuietFieldGroup>
  );
}
