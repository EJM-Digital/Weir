import { afterEach, describe, expect, it, vi } from "vitest";

import {
  bringBackRecoveryPrompt,
  putRecoveryPromptAside,
  recoveryPromptPutAside,
} from "./recovery-prompt";

afterEach(() => {
  vi.restoreAllMocks();
  window.sessionStorage.clear();
});

function blockStorage() {
  vi.spyOn(window, "sessionStorage", "get").mockImplementation(() => {
    throw new DOMException("blocked", "SecurityError");
  });
}

describe("putting the recovery-codes prompt aside", () => {
  it("is remembered for this sign-in and brought back by the next one", () => {
    expect(recoveryPromptPutAside()).toBe(false);

    putRecoveryPromptAside();
    expect(recoveryPromptPutAside()).toBe(true);

    bringBackRecoveryPrompt();
    expect(recoveryPromptPutAside()).toBe(false);
  });

  it("is never claimed when site data is blocked, and asking never throws", () => {
    blockStorage();

    expect(() => putRecoveryPromptAside()).not.toThrow();
    expect(() => bringBackRecoveryPrompt()).not.toThrow();
    expect(recoveryPromptPutAside()).toBe(false);
  });

  it("reads a refused look, a refused write and a refused removal as nothing remembered", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new DOMException("blocked", "SecurityError");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new DOMException("blocked", "SecurityError");
    });
    vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
      throw new DOMException("blocked", "SecurityError");
    });

    expect(recoveryPromptPutAside()).toBe(false);
    expect(() => putRecoveryPromptAside()).not.toThrow();
    expect(() => bringBackRecoveryPrompt()).not.toThrow();
  });
});
