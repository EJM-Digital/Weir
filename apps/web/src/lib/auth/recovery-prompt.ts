/**
 * Whether the prompt to make recovery codes has been put aside for this sign-in. It comes back the next time someone signs in, and
 * System › Security keeps asking until there is a saved set, so putting it aside here is only "not now".
 */

const KEY = "mm:recovery-prompt-put-aside";

function store(): Storage | null {
  // Private windows and blocked site data make this throw rather than return null.
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

export function recoveryPromptPutAside(): boolean {
  try {
    return store()?.getItem(KEY) === "1";
  } catch {
    return false;
  }
}

export function putRecoveryPromptAside(): void {
  try {
    store()?.setItem(KEY, "1");
  } catch {
    /* Without storage the prompt simply shows again on the next page load. */
  }
}

export function bringBackRecoveryPrompt(): void {
  try {
    store()?.removeItem(KEY);
  } catch {
    /* ignored */
  }
}
