/** True when the account has no set of recovery codes it can count on: none left, or a set nobody said they saved. */
export function needsRecoveryCodes(status: {
  remaining: number;
  confirmed: boolean;
}): boolean {
  return status.remaining === 0 || !status.confirmed;
}
