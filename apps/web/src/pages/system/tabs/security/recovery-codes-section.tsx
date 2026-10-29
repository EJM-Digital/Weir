import { useState } from "react";

import { SaveRecoveryCodes } from "../../../../components/auth/save-recovery-codes";
import { Chip } from "../../../../components/panels/chip";
import { Panel } from "../../../../components/panels/panel";
import { LoadError } from "../../../../components/shared/load-error";
import { errorMessage } from "../../../../lib/api/error-message";
import {
  useNewRecoveryCodesMutation,
  useRecoveryCodesQuery,
} from "../../../../lib/auth/queries";
import { needsRecoveryCodes } from "../../../../lib/auth/recovery-status";
import { RECOVERY_TEXT } from "../../../../lib/auth/recovery-text";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { RevealablePasswordField } from "./security-field";

/**
 * System › Security: how many recovery codes are left, a nudge until there is a saved set, and a new set that needs the current
 * password. A new set voids every code before it, and its codes are shown here once.
 */
export function RecoveryCodesSection() {
  const status = useRecoveryCodesQuery();
  const makeSet = useNewRecoveryCodesMutation();
  const [password, setPassword] = useState("");
  // Bumped on each submit, so the password field remounts with its text hidden again.
  const [attempt, setAttempt] = useState(0);
  const [missing, setMissing] = useState(false);
  const [shown, setShown] = useState<readonly string[] | null>(null);
  const busy = makeSet.isPending;

  const submit = () => {
    setMissing(password === "");
    if (password === "") return;
    makeSet.mutate(password, {
      onSuccess: (made) => {
        setPassword("");
        setShown(made.codes);
      },
      onSettled: () => setAttempt((n) => n + 1),
    });
  };

  return (
    <Panel
      title={RECOVERY_TEXT.settingsTitle}
      headingId="suite-security-recovery-heading"
      padded
      dataTestId="suite-security-recovery"
    >
      {shown ? (
        <SaveRecoveryCodes codes={shown} onSaved={() => setShown(null)} />
      ) : (
        <div className="mm-account-form">
          <p className="mm-account-form__help">{RECOVERY_TEXT.settingsHelp}</p>
          {status.isError ? (
            <LoadError thing={RECOVERY_TEXT.loadThing} error={status.error} />
          ) : status.data ? (
            <RecoveryStatus status={status.data} />
          ) : null}
          <p className="mm-account-form__help">{RECOVERY_TEXT.noWayBack}</p>
          <h3 className="mm-account-form__title">
            {RECOVERY_TEXT.settingsMakeHeading}
          </h3>
          <p className="mm-account-form__help">
            {RECOVERY_TEXT.settingsMakeHelp}
          </p>
          <RevealablePasswordField
            key={attempt}
            label={RECOVERY_TEXT.settingsPasswordLabel}
            revealLabel="current password"
            placeholder={RECOVERY_TEXT.settingsPasswordPlaceholder}
            autoComplete="current-password"
            value={password}
            onChange={setPassword}
            disabled={busy}
          />
          {makeSet.isError ? (
            <p
              className="mm-status-text text-sm"
              data-status="broken"
              role="alert"
            >
              {errorMessage(makeSet.error, RECOVERY_TEXT.makeFailed)}
            </p>
          ) : null}
          {missing ? (
            <p
              className="mm-status-text text-sm"
              data-status="broken"
              role="alert"
            >
              {RECOVERY_TEXT.settingsPasswordMissing}
            </p>
          ) : null}
          <div className="mm-account-form__action">
            <button
              type="button"
              data-testid="recovery-make"
              className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
              disabled={busy}
              onClick={submit}
            >
              {busy ? RECOVERY_TEXT.settingsMaking : RECOVERY_TEXT.settingsMake}
            </button>
          </div>
        </div>
      )}
    </Panel>
  );
}

/** How many codes are left; when there is no set to count on, says so and keeps asking for one. */
function RecoveryStatus({
  status,
}: {
  status: { remaining: number; confirmed: boolean };
}) {
  if (!needsRecoveryCodes(status)) {
    return (
      <p className="text-sm text-mm-text1" data-testid="recovery-left">
        {RECOVERY_TEXT.codesLeft(status.remaining)}
      </p>
    );
  }
  return (
    <div data-testid="recovery-nudge">
      <Chip meaning="attention">
        {status.remaining === 0
          ? RECOVERY_TEXT.settingsNone
          : RECOVERY_TEXT.settingsNotSaved}
      </Chip>
      <p className="mm-account-form__help">{RECOVERY_TEXT.settingsNudge}</p>
    </div>
  );
}
