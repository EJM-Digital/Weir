import { useCurrentSessionQuery } from "../../../../lib/auth/queries";
import { useSecurityOverviewQuery } from "../../../../lib/settings/queries";
import { AccountSection } from "./account-section";
import { RecoveryCodesSection } from "./recovery-codes-section";
import { ActiveSessionsSection } from "./active-sessions-section";
import { HomeNetworkSection } from "./home-network-section";
import { SignInProtectionSection } from "./sign-in-protection-section";
import { SignInSection } from "./sign-in-section";

/** System › Security: this sign-in and every other, how sign-in is protected, who may skip it, then the account itself and its recovery codes. */
export function SecurityTab() {
  const currentSessionQ = useCurrentSessionQuery();
  const overviewQ = useSecurityOverviewQuery();

  return (
    <div className="mm-sys-stack" data-testid="suite-settings-security">
      <div className="mm-sys-grid">
        <div className="mm-sys-column">
          <SignInSection />
          <ActiveSessionsSection enabled={currentSessionQ.data !== null} />
        </div>
        <div className="mm-sys-column">
          <SignInProtectionSection overviewQ={overviewQ} />
          <HomeNetworkSection />
        </div>
      </div>
      <AccountSection />
      <RecoveryCodesSection />
    </div>
  );
}
