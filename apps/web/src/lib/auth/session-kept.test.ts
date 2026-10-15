import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  clearLoginSucceeded,
  markLoginSucceeded,
  sessionWasNotKept,
} from "./session-kept";

const SIGNED_IN_AT = Date.UTC(2026, 9, 15, 9, 0, 0);

describe("sessionWasNotKept", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(SIGNED_IN_AT);
    clearLoginSucceeded();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("is false when no sign-in happened", () => {
    expect(sessionWasNotKept()).toBe(false);
  });

  it("is true when a sign-in succeeded moments ago", () => {
    markLoginSucceeded();
    vi.setSystemTime(SIGNED_IN_AT + 1_000);
    expect(sessionWasNotKept()).toBe(true);
  });

  it("is consumed, so the explanation shows once", () => {
    markLoginSucceeded();
    expect(sessionWasNotKept()).toBe(true);
    expect(sessionWasNotKept()).toBe(false);
  });

  it("is true up to the last millisecond of the ten second window and false from the end of it", () => {
    markLoginSucceeded();
    expect(sessionWasNotKept(SIGNED_IN_AT + 9_999)).toBe(true);

    markLoginSucceeded();
    expect(sessionWasNotKept(SIGNED_IN_AT + 10_000)).toBe(false);
  });

  it("is false once the window has passed, so a normal expiry is not mislabelled", () => {
    markLoginSucceeded();
    vi.setSystemTime(SIGNED_IN_AT + 60_000);
    expect(sessionWasNotKept()).toBe(false);
  });
});
