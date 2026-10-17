import { useState, type RefObject } from "react";

import { Field } from "../../../../components/shared/field";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { eventLabel } from "./alert-events";

/** `url` is left out when the saved address is to be kept. */
export type ChannelFormData = {
  label: string;
  provider: string;
  url?: string;
  events: string[];
  enabled: boolean;
};

const NEW_CHANNEL = {
  label: "",
  provider: "webhook",
  url: "",
  events: ["job_failed"],
  enabled: true,
} satisfies ChannelFormData;

const OPTION_CLASS =
  "flex cursor-pointer items-center gap-2 text-sm text-mm-text2";
export const ALERT_CHECKBOX_CLASS = "h-4 w-4 shrink-0 accent-mm-accent";

/**
 * Adds or edits one channel: where it posts, and which events it hears about. An alert that is being edited is given its
 * `savedAddress` as the server shows it, with the secret part hidden: the address itself is typed again only to replace it.
 */
export function ChannelForm({
  initial = NEW_CHANNEL,
  savedAddress,
  supportedEvents,
  onSave,
  onCancel,
  saving,
  saveError,
  labelRef,
}: {
  initial?: ChannelFormData;
  savedAddress?: string;
  supportedEvents: string[];
  onSave: (data: ChannelFormData) => void;
  onCancel: () => void;
  saving: boolean;
  saveError: string | null;
  /** Reaches the label field, for a drawer that focuses it as it opens. */
  labelRef?: RefObject<HTMLInputElement | null>;
}) {
  const [draft, setDraft] = useState({ ...initial, url: initial.url ?? "" });
  const [replacingAddress, setReplacingAddress] = useState(false);
  const addressShown = savedAddress === undefined || replacingAddress;
  const change = <K extends keyof ChannelFormData>(
    key: K,
    value: ChannelFormData[K],
  ) => setDraft((current) => ({ ...current, [key]: value }));
  const toggleEvent = (event: string) =>
    change(
      "events",
      draft.events.includes(event)
        ? draft.events.filter((e) => e !== event)
        : [...draft.events, event],
    );
  const incomplete =
    !draft.label.trim() ||
    (addressShown && !draft.url.trim()) ||
    draft.events.length === 0;
  const keepSavedAddress = () => {
    change("url", "");
    setReplacingAddress(false);
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        const { url, ...rest } = draft;
        onSave(addressShown ? { ...rest, url } : rest);
      }}
      className="space-y-4"
    >
      <div className="mm-field-row">
        <Field label="Label" width="medium">
          <input
            ref={labelRef}
            type="text"
            className="mm-input"
            value={draft.label}
            onChange={(e) => change("label", e.target.value)}
            placeholder="e.g. Discord alerts"
            required
            maxLength={255}
            disabled={saving}
          />
        </Field>
        <Field label="Provider" width="medium">
          <select
            className="mm-input"
            value={draft.provider}
            onChange={(e) => change("provider", e.target.value)}
            disabled={saving}
          >
            <option value="webhook">Generic webhook (JSON POST)</option>
            <option value="discord">Discord webhook</option>
          </select>
        </Field>
      </div>

      {addressShown ? (
        <>
          <Field label="Webhook URL" width="wide">
            <input
              type="url"
              autoComplete="off"
              className="mm-input"
              value={draft.url}
              onChange={(e) => change("url", e.target.value)}
              placeholder="https://..."
              required
              disabled={saving}
            />
          </Field>
          {savedAddress === undefined ? null : (
            <button
              type="button"
              className={mmActionButtonClass({ variant: "tertiary" })}
              disabled={saving}
              onClick={keepSavedAddress}
            >
              Keep the saved address
            </button>
          )}
        </>
      ) : (
        <div className="mm-field mm-field--wide">
          <span className="mm-field__label">Webhook URL</span>
          <code className="font-mono text-sm" data-testid="alert-saved-address">
            {savedAddress}
          </code>
          <span className="mm-field__hint">
            Its secret part is kept out of sight. Replace the address to change
            it.
          </span>
          <button
            type="button"
            className={mmActionButtonClass({ variant: "secondary" })}
            disabled={saving}
            onClick={() => setReplacingAddress(true)}
          >
            Replace the address
          </button>
        </div>
      )}

      <fieldset>
        <legend className="mm-field__label mb-2">Trigger events</legend>
        <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
          {supportedEvents.map((event) => (
            <label key={event} className={OPTION_CLASS}>
              <input
                type="checkbox"
                className={ALERT_CHECKBOX_CLASS}
                checked={draft.events.includes(event)}
                onChange={() => toggleEvent(event)}
                disabled={saving}
              />
              {eventLabel(event)}
            </label>
          ))}
        </div>
      </fieldset>

      <label className={OPTION_CLASS}>
        <input
          type="checkbox"
          className={ALERT_CHECKBOX_CLASS}
          checked={draft.enabled}
          onChange={(e) => change("enabled", e.target.checked)}
          disabled={saving}
        />
        Enabled
      </label>

      {saveError ? (
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {saveError}
        </p>
      ) : null}

      <div className="flex gap-2">
        <button
          type="submit"
          className={mmActionButtonClass({ variant: "primary" })}
          disabled={saving || incomplete}
        >
          {saving ? "Saving…" : "Save alert"}
        </button>
        <button
          type="button"
          className={mmActionButtonClass({ variant: "tertiary" })}
          disabled={saving}
          onClick={onCancel}
        >
          Cancel
        </button>
      </div>
    </form>
  );
}
