import { afterEach, describe, expect, it, vi } from "vitest";

import { readLastLibrary, saveLastLibrary } from "./last-library";

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

describe("the last library picked", () => {
  it("is nothing until a library has been picked", () => {
    expect(readLastLibrary()).toBeNull();
  });

  it("comes back as the id that was saved", () => {
    saveLastLibrary(4);
    expect(readLastLibrary()).toBe(4);
  });

  it("is nothing when the browser will not say", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new DOMException("blocked", "SecurityError");
    });
    expect(readLastLibrary()).toBeNull();
  });

  it("ignores a stored value that is not a library id", () => {
    localStorage.setItem("weir-library-last", "movies");
    expect(readLastLibrary()).toBeNull();
    localStorage.setItem("weir-library-last", "-3");
    expect(readLastLibrary()).toBeNull();
  });
});
