import { useState } from "react";

import {
  useHomeNetworkSignInMutation,
  useHomeNetworkSignInQuery,
} from "../../lib/settings/home-network-sign-in";

/**
 * Whether devices on the home network skip sign-in, asked in setup. On a first run the question starts unanswered and
 * stays off until it is answered; a setup opened again starts from what is saved. Changing the answer asks for the current
 * password, as it does on Security, and the answer is saved with the rest of setup.
 */
export function useWizardHomeNetwork(firstRun: boolean) {
  const saved = useHomeNetworkSignInQuery().data;
  const save = useHomeNetworkSignInMutation();
  const [chosen, setChosen] = useState<boolean | null>(null);
  const [password, setPassword] = useState("");
  const answer = chosen ?? (firstRun ? null : (saved?.enabled ?? null));
  const changes =
    saved !== undefined && answer !== null && answer !== saved.enabled;

  return {
    /** Setup asks once it is known what is saved. */
    asked: saved !== undefined,
    answer,
    choose: setChosen,
    password,
    setPassword,
    /** Whether the answer is a change, which has to be confirmed with the password. */
    needsPassword: changes,
    /** Why setup cannot be finished as it stands, or null. */
    blocker:
      changes && password === ""
        ? "Type your password to save your answer about skipping sign-in, or choose the answer that is saved."
        : null,
    /** Saves the answer when it differs from what is saved. */
    saveAnswer: async () => {
      if (saved === undefined || answer === null || answer === saved.enabled)
        return;
      await save.mutateAsync({
        enabled: answer,
        extra_networks: saved.extra_networks,
        allow_list: saved.allow_list,
        current_password: password,
      });
    },
  };
}
