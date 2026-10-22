import { describe, expect, it } from "vitest";

import { lastResult } from "./connection-health";

describe("what a connection's last test says", () => {
  it("is the result when there is a time behind it", () => {
    expect(
      lastResult({
        enabled: true,
        last_test_at: "2026-10-02T12:00:00Z",
        last_test_ok: true,
      }),
    ).toBe(true);
    expect(
      lastResult({
        enabled: true,
        last_test_at: "2026-10-02T12:00:00Z",
        last_test_ok: false,
      }),
    ).toBe(false);
  });

  it("is nothing when the result has no time behind it", () => {
    expect(
      lastResult({ enabled: true, last_test_at: null, last_test_ok: true }),
    ).toBeNull();
  });

  it("is nothing when the test left no result", () => {
    expect(
      lastResult({
        enabled: true,
        last_test_at: "2026-10-02T12:00:00Z",
        last_test_ok: null,
      }),
    ).toBeNull();
    expect(
      lastResult({ enabled: true, last_test_at: "2026-10-02T12:00:00Z" }),
    ).toBeNull();
  });
});
