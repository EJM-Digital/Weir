import { useState } from "react";

import { Panel } from "../../../../components/panels/panel";
import { LoadError } from "../../../../components/shared/load-error";
import { errorMessage } from "../../../../lib/api/error-message";
import {
  HOME_NETWORK_WORDS,
  useHomeNetworkSignInMutation,
  useHomeNetworkSignInQuery,
} from "../../../../lib/settings/home-network-sign-in";
import type { HomeNetworkSignIn } from "../../../../lib/settings/types";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { AddressListField } from "./address-list-field";
import { RevealablePasswordField } from "./security-field";

const sameList = (a: readonly string[], b: readonly string[]) =>
  a.length === b.length && a.every((entry, index) => entry === b[index]);

function HomeNetworkForm({
  saved,
  onSaved,
}: {
  saved: HomeNetworkSignIn;
  onSaved: (message: string) => void;
}) {
  const save = useHomeNetworkSignInMutation();
  const [enabled, setEnabled] = useState(saved.enabled);
  const [extra, setExtra] = useState<string[]>(saved.extra_networks);
  const [allow, setAllow] = useState<string[]>(saved.allow_list);
  const [password, setPassword] = useState("");
  const busy = save.isPending;
  const dirty =
    enabled !== saved.enabled ||
    !sameList(extra, saved.extra_networks) ||
    !sameList(allow, saved.allow_list);

  const submit = () =>
    save.mutate(
      {
        enabled,
        extra_networks: extra,
        allow_list: allow,
        current_password: password,
      },
      {
        onSuccess: (data) =>
          onSaved(
            data.enabled
              ? HOME_NETWORK_WORDS.savedOn
              : HOME_NETWORK_WORDS.savedOff,
          ),
      },
    );

  return (
    <div className="mm-account-form">
      <p className="mm-quiet-note" data-testid="home-network-words">
        {HOME_NETWORK_WORDS.text}
      </p>
      <label className="mm-library-toggle">
        <input
          className="mt-0.5"
          type="checkbox"
          role="switch"
          checked={enabled}
          disabled={busy}
          onChange={(e) => setEnabled(e.target.checked)}
        />
        <span>
          <span className="mm-library-toggle__label">
            {HOME_NETWORK_WORDS.switch}
          </span>
          <span className="mm-library-toggle__hint">
            {HOME_NETWORK_WORDS.switchHelp}
          </span>
        </span>
      </label>
      {saved.enabled && saved.warning ? (
        <p
          className="mm-status-text text-sm"
          data-status="attention"
          role="alert"
          data-testid="home-network-warning"
        >
          {saved.warning}
        </p>
      ) : null}
      {enabled ? (
        <>
          <AddressListField
            label="Your own networks (optional)"
            help="Weir counts these as local too, on top of the ones above. Leave it empty if your devices are on private addresses already."
            entries={extra}
            onChange={setExtra}
            disabled={busy}
          />
          <AddressListField
            label="Narrow this to specific addresses (optional)"
            help="Leave empty and every local device skips sign-in. Add addresses here and only those skip sign-in -- still only if they are also local."
            entries={allow}
            onChange={setAllow}
            disabled={busy}
          />
        </>
      ) : null}
      {dirty ? (
        <RevealablePasswordField
          label="Current password"
          revealLabel="current password"
          placeholder="Confirm it is you"
          autoComplete="current-password"
          value={password}
          onChange={setPassword}
          disabled={busy}
        />
      ) : null}
      {save.isError ? (
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {errorMessage(
            save.error,
            "Could not save the home network sign-in setting.",
          )}
        </p>
      ) : null}
      <div className="mm-account-form__action">
        <button
          type="button"
          className={`${mmActionButtonClass({ variant: "primary" })} mm-sys-btn`}
          disabled={busy || !dirty || password === ""}
          onClick={submit}
        >
          {busy ? "Saving…" : "Save"}
        </button>
      </div>
    </div>
  );
}

/**
 * System › Security: whether devices on the home network skip sign-in. Changing it asks for the current password, even of
 * a device that skipped sign-in.
 */
export function HomeNetworkSection() {
  const query = useHomeNetworkSignInQuery();
  const [message, setMessage] = useState<string | null>(null);
  const saved = query.data;

  return (
    <Panel
      title={HOME_NETWORK_WORDS.title}
      headingId="suite-security-home-network-heading"
      padded
      dataTestId="suite-security-home-network"
    >
      {saved ? (
        <>
          {/* Saving replaces the form, so what was typed is gone and the password with it. */}
          <HomeNetworkForm
            key={JSON.stringify(saved)}
            saved={saved}
            onSaved={setMessage}
          />
          {message ? (
            <p
              className="mm-status-text text-sm"
              data-status="done"
              role="status"
              data-testid="home-network-saved"
            >
              {message}
            </p>
          ) : null}
        </>
      ) : query.isError ? (
        <LoadError
          thing="the home network sign-in setting"
          error={query.error}
        />
      ) : (
        <p className="mm-quiet-note">Loading…</p>
      )}
    </Panel>
  );
}
