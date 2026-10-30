/** Every sign-in and session query key. */
export const authKeys = {
  me: ["auth", "me"] as const,
  session: ["auth", "session"] as const,
  sessions: ["auth", "sessions"] as const,
  bootstrap: ["auth", "bootstrap-status"] as const,
  recoveryCodes: ["auth", "recovery-codes"] as const,
};
