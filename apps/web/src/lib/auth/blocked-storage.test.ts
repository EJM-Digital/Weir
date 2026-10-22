import { afterEach, describe, expect, it, vi } from "vitest";

import { sessionWasNotKept, markLoginSucceeded } from "./session-kept";
import { markSignedIn, wasSignedIn } from "./signed-in-before";

afterEach(() => {
  vi.restoreAllMocks();
  window.sessionStorage.clear();
  window.localStorage.clear();
});

function blockStorage(name: "sessionStorage" | "localStorage") {
  vi.spyOn(window, name, "get").mockImplementation(() => {
    throw new DOMException("blocked", "SecurityError");
  });
}

function refuseReads() {
  vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
    throw new DOMException("blocked", "SecurityError");
  });
}

describe("a browser that will not remember a sign-in", () => {
  it("never reports a session that was not kept when site data is blocked", () => {
    blockStorage("sessionStorage");

    expect(() => markLoginSucceeded()).not.toThrow();
    expect(sessionWasNotKept()).toBe(false);
  });

  it("reads a refused look at what was kept as nothing kept", () => {
    refuseReads();

    expect(sessionWasNotKept()).toBe(false);
  });

  it("reads a marker that is not a time as nothing kept", () => {
    window.sessionStorage.setItem("mm:login-succeeded-at", "later");

    expect(sessionWasNotKept()).toBe(false);
  });

  it("does not claim someone was signed in before when site data is blocked", () => {
    blockStorage("localStorage");

    expect(() => markSignedIn()).not.toThrow();
    expect(wasSignedIn()).toBe(false);
  });

  it("does not claim someone was signed in before when the look is refused", () => {
    refuseReads();

    expect(wasSignedIn()).toBe(false);
  });
});
