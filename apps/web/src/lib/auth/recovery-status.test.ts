import { describe, expect, it } from "vitest";

import { needsRecoveryCodes } from "./recovery-status";
import { RECOVERY_TEXT } from "./recovery-text";

describe("whether an account needs recovery codes", () => {
  it("does when it has none, or a set nobody said they saved, and not when a saved set has codes left", () => {
    expect(needsRecoveryCodes({ remaining: 0, confirmed: false })).toBe(true);
    expect(needsRecoveryCodes({ remaining: 0, confirmed: true })).toBe(true);
    expect(needsRecoveryCodes({ remaining: 10, confirmed: false })).toBe(true);
    expect(needsRecoveryCodes({ remaining: 1, confirmed: true })).toBe(false);
  });
});

describe("the words for how many codes are left", () => {
  it("counts them the way every other count in Weir is", () => {
    expect(RECOVERY_TEXT.codesLeft(10)).toBe("10 of 10 recovery codes left.");
    expect(RECOVERY_TEXT.codesLeft(1)).toBe("1 of 10 recovery codes left.");
    expect(RECOVERY_TEXT.codesLeft(0)).toBe("0 of 10 recovery codes left.");
  });
});
