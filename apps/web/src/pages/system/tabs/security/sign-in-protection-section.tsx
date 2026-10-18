import { Panel } from "../../../../components/panels/panel";
import { LoadError } from "../../../../components/shared/load-error";
import type { useSecurityOverviewQuery } from "../../../../lib/settings/queries";
import { plural } from "../../../../lib/ui/mm-plural";
import { signInProtections, type Protection } from "./security-facts";

const MARK_SIZE = 16;

/** "N protections in force" only when every row is actually healthy; otherwise it says what needs a look. */
function protectionSummary(protections: readonly Protection[]): string {
  const attention = protections.filter((p) => p.needsAttention).length;
  if (attention > 0) {
    return `${protections.length} protections — ${plural(attention, "row needs", "rows need")} attention`;
  }
  return `${protections.length} protections in force`;
}

function Mark({ attention }: { attention: boolean }) {
  return (
    <svg
      className="mm-protection__mark"
      xmlns="http://www.w3.org/2000/svg"
      width={MARK_SIZE}
      height={MARK_SIZE}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.4"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {attention ? (
        <>
          <path d="M12 4 2.5 20h19L12 4Z" />
          <path d="M12 10v4" />
          <path d="M12 17.5v.01" />
        </>
      ) : (
        <path d="m5 12.5 4.5 4.5L19 7.5" />
      )}
    </svg>
  );
}

function ProtectionRows({ protections }: { protections: Protection[] }) {
  return (
    <ul className="mm-protection__list">
      {protections.map((protection) => (
        <li
          key={protection.label}
          className="mm-protection"
          data-status={protection.needsAttention ? "attention" : "done"}
        >
          <Mark attention={protection.needsAttention} />
          <span className="mm-protection__name">{protection.label}</span>
          <span className="mm-protection__value">{protection.value}</span>
        </li>
      ))}
    </ul>
  );
}

/**
 * The protections in force, open, with any that need a person first and what to do about them. Read-only: they come
 * from startup configuration.
 */
export function SignInProtectionSection({
  overviewQ,
}: {
  overviewQ: ReturnType<typeof useSecurityOverviewQuery>;
}) {
  const overview = overviewQ.data;
  const protections = overview ? signInProtections(overview) : [];
  const attention = protections.filter((p) => p.needsAttention);
  const fine = protections.filter((p) => !p.needsAttention);
  const note = overview?.restart_required_note;

  return (
    <Panel
      title="How sign-in is protected"
      headingId="suite-security-protection-heading"
      padded
      count={overview ? protectionSummary(protections) : undefined}
      dataTestId="suite-security-posture"
    >
      {overview ? (
        <>
          {attention.length > 0 ? (
            <div className="mm-protection__attention" data-status="attention">
              <ProtectionRows protections={attention} />
              {note ? <p className="mm-protection__advice">{note}</p> : null}
            </div>
          ) : null}
          <ProtectionRows protections={fine} />
          {attention.length === 0 && note ? (
            <p className="mm-sys-note">{note}</p>
          ) : null}
        </>
      ) : overviewQ.isError ? (
        <LoadError
          thing="the server security overview"
          error={overviewQ.error}
        />
      ) : (
        <p className="mm-quiet-note">Loading server security overview…</p>
      )}
    </Panel>
  );
}
