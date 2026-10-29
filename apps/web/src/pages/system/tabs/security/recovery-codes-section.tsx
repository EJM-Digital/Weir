import { useState } from "react";

import { PasswordDialog } from "../../../../components/auth/password-dialog";
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

/**
 * System › Security: how many recovery codes are left, a nudge until there is a saved set, and new codes that need the current
 * password, asked for in a dialog. New codes void every code before them, and are shown here once.
 */
export function RecoveryCodesSection() {
  const status = useRecoveryCodesQuery();
  const makeSet = useNewRecoveryCodesMutation();
  const [asking, setAsking] = useState(false);
  const [shown, setShown] = useState<readonly string[] | null>(null);
  const hasSet = status.data !== undefined && status.data.remaining > 0;

  const confirm = (password: string) => {
    makeSet.mutate(password, {
      onSuccess: (made) => {
        setAsking(false);
        setShown(made.codes);
      },
    });
  };

  const cancel = () => {
    makeSet.reset();
    setAsking(false);
  };

  return (
    <Panel
      title={RECOVERY_TEXT.settingsTitle}
      headingId="suite-security-recovery-heading"
      padded
      dataTestId="suite-security-recovery"
    >
      {shown ? (
        <SaveRecoveryCodes
          codes={shown}
          onSaved={() => setShown(null)}
          finishLabel={RECOVERY_TEXT.done}
        />
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
          <div className="mm-account-form__action">
            <button
              type="button"
              data-testid="recovery-make"
              className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
              onClick={() => setAsking(true)}
            >
              {hasSet
                ? RECOVERY_TEXT.settingsMakeNew
                : RECOVERY_TEXT.settingsMake}
            </button>
          </div>
        </div>
      )}
      {asking ? (
        <PasswordDialog
          busy={makeSet.isPending}
          error={
            makeSet.isError
              ? errorMessage(makeSet.error, RECOVERY_TEXT.makeFailed)
              : null
          }
          onConfirm={confirm}
          onCancel={cancel}
        />
      ) : null}
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
