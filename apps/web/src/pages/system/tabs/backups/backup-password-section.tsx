import { useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";

import { Panel } from "../../../../components/panels/panel";
import { quietActionRowClass } from "../../../../components/shared/quiet-section";
import { SettingRow } from "../../../../components/shared/settings-group";
import { errorMessage } from "../../../../lib/api/error-message";
import { settingsKeys } from "../../../../lib/settings/query-keys";
import { putBackupPassword } from "../../../../lib/settings/settings-api";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";

const NOT_SET_WORDS =
  "No backup password is set, so backups leave out your connections' API keys, passwords and alert addresses. Set one to include them, locked so only the password opens them.";
const SET_WORDS =
  "Backups lock your connections' API keys, passwords and alert addresses with this password. Restoring one asks for it. Weir cannot recover it, so keep it somewhere safe.";
const OLDER_BACKUPS_WORDS =
  "Changing the password locks the backups made from then on. Backups already made keep the password they were made with.";

/**
 * The password that locks the secrets in each backup. Typed twice, because a mistyped password would lock the secrets in every
 * backup with something nobody knows. Weir shows whether one is set and never the password itself.
 */
export function BackupPasswordSection({
  passwordSet,
}: {
  passwordSet: boolean;
}) {
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    if (password !== confirmation) {
      setProblem(
        "The two backup passwords do not match. Type the same password in both boxes.",
      );
      return;
    }
    setProblem(null);
    setBusy(true);
    try {
      await putBackupPassword(password, confirmation);
      setPassword("");
      setConfirmation("");
      setSaved(true);
      await queryClient.invalidateQueries({
        queryKey: settingsKeys.configurationBackups,
      });
    } catch (error) {
      setProblem(errorMessage(error, "Could not set the backup password."));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel
      title="Backup password"
      headingId="suite-settings-backup-password-heading"
      padded
      note={
        <span data-testid="backup-password-state">
          {passwordSet ? "Set" : "Not set"}
        </span>
      }
    >
      <form onSubmit={(event) => void submit(event)} noValidate>
        <p className="mm-quiet-note" data-testid="backup-password-words">
          {passwordSet ? SET_WORDS : NOT_SET_WORDS}
        </p>
        <div className="mm-setgroup__rows">
          <SettingRow
            label={passwordSet ? "New backup password" : "Backup password"}
            htmlFor="backup-password"
          >
            <input
              id="backup-password"
              type="password"
              autoComplete="new-password"
              className="mm-input mm-sys-field"
              value={password}
              disabled={busy}
              onChange={(e) => setPassword(e.target.value)}
            />
          </SettingRow>
          <SettingRow label="Type it again" htmlFor="backup-password-confirm">
            <input
              id="backup-password-confirm"
              type="password"
              autoComplete="new-password"
              className="mm-input mm-sys-field"
              value={confirmation}
              disabled={busy}
              onChange={(e) => setConfirmation(e.target.value)}
            />
          </SettingRow>
        </div>
        {passwordSet ? (
          <p className="mm-quiet-note">{OLDER_BACKUPS_WORDS}</p>
        ) : null}
        <div className={quietActionRowClass}>
          <button
            type="submit"
            className={`${mmActionButtonClass({ variant: "primary" })} mm-sys-btn`}
            disabled={busy || password === "" || confirmation === ""}
          >
            {busy
              ? "Saving…"
              : passwordSet
                ? "Change backup password"
                : "Set backup password"}
          </button>
          {problem ? (
            <p
              className="mm-status-text text-sm"
              data-status="broken"
              role="alert"
              data-testid="backup-password-error"
            >
              {problem}
            </p>
          ) : saved ? (
            <p
              className="mm-status-text text-sm"
              data-status="done"
              role="status"
            >
              Backup password saved. Backups from now on use it.
            </p>
          ) : null}
        </div>
      </form>
    </Panel>
  );
}
