import { Chip } from "../../../../components/panels/chip";
import { Panel } from "../../../../components/panels/panel";
import { useCurrentSessionQuery } from "../../../../lib/auth/queries";
import { useSecurityOverviewQuery } from "../../../../lib/settings/queries";
import { currentSignInRows } from "./security-facts";

/** System › Security: how this browser is signed in, as a few short facts. */
export function SignInSection() {
  const currentSessionQ = useCurrentSessionQuery();
  const overviewQ = useSecurityOverviewQuery();
  const rows = currentSignInRows(
    currentSessionQ.data,
    overviewQ.data,
    currentSessionQ.isError,
  );

  return (
    <Panel
      title="Sign-in"
      headingId="suite-security-current-sign-in-heading"
      padded
    >
      <dl
        className="mm-kv mm-sys-facts"
        aria-label="How this browser is signed in"
      >
        {rows.map((row) => (
          <div key={row.label}>
            <dt>{row.label}</dt>
            <dd title={row.detail}>
              {row.meaning ? (
                <Chip meaning={row.meaning}>{row.value}</Chip>
              ) : (
                row.value
              )}
            </dd>
          </div>
        ))}
      </dl>
    </Panel>
  );
}
