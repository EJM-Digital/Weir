import { HOME_NETWORK_WORDS } from "../../lib/settings/home-network-sign-in";
import { RevealablePasswordField } from "../system/tabs/security/security-field";
import { WizardSection } from "./setup-wizard-parts";

const ANSWERS = [
  { value: true, label: "Yes" },
  { value: false, label: "No" },
] as const;

/** Asks plainly whether devices on the home network may skip sign-in. Unanswered until the person chooses; unanswered means off. */
export function WizardHomeNetworkSection({
  answer,
  onAnswer,
  needsPassword,
  password,
  onPassword,
  disabled,
}: {
  answer: boolean | null;
  onAnswer: (answer: boolean) => void;
  needsPassword: boolean;
  password: string;
  onPassword: (password: string) => void;
  disabled: boolean;
}) {
  return (
    <WizardSection
      headingId="setup-wizard-home-network-heading"
      title="How you sign in"
      description="Always sign in, or skip sign-in for devices on your home network. You can change this later in System, Security."
    >
      <fieldset className="mm-wizard-choices">
        <legend className="mm-wizard-label">
          {HOME_NETWORK_WORDS.question}
        </legend>
        {ANSWERS.map((option) => (
          <label key={option.label} className="mm-wizard-choice">
            <input
              type="radio"
              name="setup-wizard-home-network"
              className="mt-0.5 h-4 w-4 shrink-0 accent-mm-accent"
              aria-label={`${HOME_NETWORK_WORDS.question} ${option.label}`}
              checked={answer === option.value}
              disabled={disabled}
              onChange={() => onAnswer(option.value)}
            />
            <span>{option.label}</span>
          </label>
        ))}
      </fieldset>
      {needsPassword ? (
        <RevealablePasswordField
          label="Your password"
          revealLabel="your password"
          placeholder="Confirm it is you"
          autoComplete="current-password"
          value={password}
          onChange={onPassword}
          disabled={disabled}
        />
      ) : null}
    </WizardSection>
  );
}
