import { WizardSection } from "./setup-wizard-parts";

/** Asks plainly whether Weir may look for new versions of itself. Off unless the person says yes. */
export function WizardUpdatesSection({
  checking,
  onChange,
  disabled,
}: {
  checking: boolean;
  onChange: (checking: boolean) => void;
  disabled: boolean;
}) {
  return (
    <WizardSection
      headingId="setup-wizard-updates-heading"
      title="Updates"
      description="Weir can check GitHub for new versions of itself and tell you when one is out. It checks nothing unless you say so. You can change this later in System, About."
    >
      <label className="flex cursor-pointer items-start gap-2.5 text-sm text-mm-text1">
        <input
          type="checkbox"
          className="mt-0.5 h-4 w-4 shrink-0 accent-mm-accent"
          checked={checking}
          disabled={disabled}
          onChange={(e) => onChange(e.target.checked)}
        />
        <span>Check for updates</span>
      </label>
    </WizardSection>
  );
}
