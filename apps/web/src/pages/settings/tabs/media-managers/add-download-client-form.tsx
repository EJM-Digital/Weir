import { useState, type RefObject } from "react";

import { Field } from "../../../../components/shared/field";
import {
  QuietSection,
  quietActionRowClass,
} from "../../../../components/shared/quiet-section";
import { errorMessage } from "../../../../lib/api/error-message";
import {
  DOWNLOAD_CLIENT_KIND_CREDENTIALS,
  DOWNLOAD_CLIENT_KIND_LABELS,
  type DownloadClientConnection,
  type DownloadClientConnectionCreate,
  type DownloadClientKind,
} from "../../../../lib/download-clients/download-clients-api";
import { useCreateDownloadClientConnection } from "../../../../lib/download-clients/queries";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { ConnectionNicknameField } from "./connection-nickname-field";

const KINDS: DownloadClientKind[] = [
  "sabnzbd",
  "nzbget",
  "qbittorrent",
  "deluge",
  "transmission",
];

type FormState = {
  kind: DownloadClientKind;
  base_url: string;
  username: string;
  password: string;
  api_key: string;
  nickname: string;
};

const EMPTY_FORM: FormState = {
  kind: "sabnzbd",
  base_url: "",
  username: "",
  password: "",
  api_key: "",
  nickname: "",
};

/** What the chosen kind's own login uses, and blank for the rest, so a field typed for a kind the person moved away from is not saved with this one. */
function credentialsOf(
  form: FormState,
): Pick<DownloadClientConnectionCreate, "username" | "password" | "api_key"> {
  const none = { username: "", password: "", api_key: "" };
  switch (DOWNLOAD_CLIENT_KIND_CREDENTIALS[form.kind]) {
    case "api_key":
      return { ...none, api_key: form.api_key };
    case "password_only":
      return { ...none, password: form.password };
    case "username_password":
      return { ...none, username: form.username, password: form.password };
  }
}

type AddDownloadClientProps = {
  onCancel: () => void;
  onCreated: (connection: DownloadClientConnection) => void;
  /** Reaches the choice of download client, for a drawer that focuses it as it opens. */
  kindRef?: RefObject<HTMLSelectElement | null>;
};

/** The fields and buttons for adding a download client, for a place that already has its own heading. */
export function AddDownloadClientFields({
  onCancel,
  onCreated,
  kindRef,
}: AddDownloadClientProps) {
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const create = useCreateDownloadClientConnection();
  const change = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm((current) => ({ ...current, [key]: value }));
  const credentials = DOWNLOAD_CLIENT_KIND_CREDENTIALS[form.kind];
  // SABnzbd's API and Deluge's Web UI always need a credential; the others can run with none.
  const missingRequiredCredential =
    (credentials === "api_key" && !form.api_key.trim()) ||
    (credentials === "password_only" && !form.password.trim());

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        create.mutate(
          {
            kind: form.kind,
            base_url: form.base_url,
            ...credentialsOf(form),
            nickname: form.nickname.trim(),
            enabled: true,
          },
          { onSuccess: onCreated },
        );
      }}
    >
      <div className="mm-quiet-stack">
        <div className="mm-field-row">
          <Field label="Which download client is it?" width="medium">
            <select
              ref={kindRef}
              data-testid="download-client-kind"
              className="mm-input"
              value={form.kind}
              onChange={(e) =>
                change("kind", e.target.value as DownloadClientKind)
              }
            >
              {KINDS.map((kind) => (
                <option key={kind} value={kind}>
                  {DOWNLOAD_CLIENT_KIND_LABELS[kind]}
                </option>
              ))}
            </select>
          </Field>
        </div>
        <Field
          label="Where to find it"
          hint="The address you use to open it in a browser."
          width="wide"
        >
          <input
            data-testid="download-client-base-url"
            autoComplete="url"
            className="mm-input"
            value={form.base_url}
            placeholder="http://192.0.2.10:8080"
            onChange={(e) => change("base_url", e.target.value)}
          />
        </Field>

        {credentials === "api_key" ? (
          <Field
            label="API key"
            hint="Weir stores this safely and never shows it again."
            width="medium"
          >
            <input
              data-testid="download-client-api-key"
              type="password"
              autoComplete="new-password"
              className="mm-input"
              value={form.api_key}
              onChange={(e) => change("api_key", e.target.value)}
            />
          </Field>
        ) : null}

        {credentials === "username_password" ? (
          <div className="mm-field-row">
            <Field label="Username" width="medium">
              <input
                data-testid="download-client-username"
                autoComplete="off"
                className="mm-input"
                value={form.username}
                onChange={(e) => change("username", e.target.value)}
              />
            </Field>
            <Field
              label="Password"
              hint="Weir stores this safely and never shows it again."
              width="medium"
            >
              <input
                data-testid="download-client-password"
                type="password"
                autoComplete="new-password"
                className="mm-input"
                value={form.password}
                onChange={(e) => change("password", e.target.value)}
              />
            </Field>
          </div>
        ) : null}

        {credentials === "password_only" ? (
          <Field
            label="Password"
            hint="Weir stores this safely and never shows it again."
            width="medium"
          >
            <input
              data-testid="download-client-password"
              type="password"
              autoComplete="new-password"
              className="mm-input"
              value={form.password}
              onChange={(e) => change("password", e.target.value)}
            />
          </Field>
        ) : null}

        <ConnectionNicknameField
          testId="download-client-nickname"
          className="mm-input"
          value={form.nickname}
          onChange={(value) => change("nickname", value)}
        />
      </div>

      {create.isError ? (
        <p
          className="mm-status-text mt-2 text-sm"
          data-status="broken"
          role="alert"
        >
          {errorMessage(create.error, "Could not add this download client.")}
        </p>
      ) : null}

      <div className={quietActionRowClass}>
        <button
          type="submit"
          data-testid="download-client-save"
          className={mmActionButtonClass({ variant: "primary" })}
          disabled={
            create.isPending ||
            !form.base_url.trim() ||
            missingRequiredCredential
          }
        >
          {create.isPending ? "Adding…" : "Add"}
        </button>
        <button
          type="button"
          className={mmActionButtonClass({ variant: "secondary" })}
          onClick={onCancel}
        >
          Cancel
        </button>
      </div>
    </form>
  );
}

/** Adding a download client in a card of its own, where the page has no drawer to put it in. */
export function AddDownloadClientForm(props: AddDownloadClientProps) {
  return (
    <QuietSection
      headingId="add-download-client-heading"
      heading="Add a download client"
      level={3}
    >
      <AddDownloadClientFields {...props} />
    </QuietSection>
  );
}
