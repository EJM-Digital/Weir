import { useId, useState } from "react";

import {
  isAddressOrSubnet,
  notAnAddressWords,
} from "../../../../lib/settings/home-network-sign-in";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";

/** A list of addresses or ranges the owner types in one at a time, each removable. A wrong entry is refused on the spot. */
export function AddressListField({
  label,
  help,
  entries,
  onChange,
  disabled,
}: {
  label: string;
  help: string;
  entries: readonly string[];
  onChange: (entries: string[]) => void;
  disabled: boolean;
}) {
  const id = useId();
  const [draft, setDraft] = useState("");
  const [problem, setProblem] = useState<string | null>(null);

  const add = () => {
    const entry = draft.trim();
    if (entry === "") return;
    if (!isAddressOrSubnet(entry)) {
      setProblem(notAnAddressWords(entry));
      return;
    }
    setProblem(null);
    setDraft("");
    if (!entries.some((item) => item.toLowerCase() === entry.toLowerCase())) {
      onChange([...entries, entry]);
    }
  };

  return (
    <div className="mm-field">
      <label className="mm-field__label" htmlFor={id}>
        {label}
      </label>
      <p className="mm-account-form__help">{help}</p>
      {entries.length > 0 ? (
        <ul className="mm-protection__list">
          {entries.map((entry) => (
            <li key={entry} className="mm-protection">
              <span className="mm-protection__name">{entry}</span>
              <button
                type="button"
                className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
                disabled={disabled}
                aria-label={`Remove ${entry} from ${label}`}
                onClick={() =>
                  onChange(entries.filter((item) => item !== entry))
                }
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      ) : null}
      <div className="mm-sys-secret">
        <input
          id={id}
          type="text"
          className="mm-input"
          placeholder="192.168.1.0/28"
          value={draft}
          disabled={disabled}
          onChange={(e) => {
            setDraft(e.target.value);
            setProblem(null);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              add();
            }
          }}
        />
        <button
          type="button"
          className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
          disabled={disabled || draft.trim() === ""}
          aria-label={`Add to ${label}`}
          onClick={add}
        >
          Add
        </button>
      </div>
      {problem ? (
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {problem}
        </p>
      ) : null}
    </div>
  );
}
