import { SaveRecoveryCodes } from "../../components/auth/save-recovery-codes";
import { AuthBrandStack } from "../../components/brand/auth-brand-stack";
import { RECOVERY_TEXT } from "../../lib/auth/recovery-text";

/**
 * The last step of creating the account: the ten recovery codes, shown once. Setup waits here until the person says they saved
 * them, so the account is never left with codes nobody has seen.
 */
export function SetupCodesStep({
  codes,
  onSaved,
}: {
  codes: readonly string[];
  onSaved: () => void;
}) {
  return (
    <main className="mm-auth-body" id="mm-main-content" tabIndex={-1}>
      <div className="mm-auth-frame">
        <AuthBrandStack />
        <div className="mm-auth-card" data-testid="setup-codes">
          <p className="mm-auth-eyebrow">First run</p>
          <h1 className="mm-auth-title">{RECOVERY_TEXT.setupHeading}</h1>
          <p className="mm-auth-lead">{RECOVERY_TEXT.setupLead}</p>
          <SaveRecoveryCodes codes={codes} onSaved={onSaved} />
        </div>
      </div>
    </main>
  );
}
