import { afterEach, expect, it, vi } from "vitest";

import { examplePath } from "./platform";

afterEach(() => {
  vi.unstubAllGlobals();
});

it("gives the posix example to a browser that is not on Windows", () => {
  vi.stubGlobal("navigator", { userAgent: "Mozilla/5.0 (X11; Linux x86_64)" });

  expect(examplePath("X:\\Media", "/media")).toBe("/media");
});
