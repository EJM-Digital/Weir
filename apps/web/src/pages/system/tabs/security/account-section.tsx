import { Panel } from "../../../../components/panels/panel";
import { ChangePasswordForm, ChangeUsernameForm } from "./account-forms";

/** System › Security: the one account, as two equal columns, each a short form that ends in its own button. */
export function AccountSection() {
  return (
    <Panel
      title="Account"
      headingId="suite-security-account-heading"
      padded
      dataTestId="suite-security-account"
    >
      <div className="mm-account">
        <ChangeUsernameForm />
        <ChangePasswordForm />
      </div>
    </Panel>
  );
}
