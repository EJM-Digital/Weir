import { afterEach, describe, expect, it, vi } from "vitest";
import {
  APP_THEME_STORAGE_KEY,
  applyAppThemeToDocument,
  currentAppTheme,
  followSystemAppTheme,
  parseAppTheme,
  persistAppTheme,
  readStoredAppTheme,
} from "./app-theme";

function stubSystemPrefersLight(matches: boolean) {
  let current = matches;
  const listeners = new Set<(event: MediaQueryListEvent) => void>();
  const list = {
    get matches() {
      return current;
    },
    addEventListener: (
      _type: string,
      listener: (event: MediaQueryListEvent) => void,
    ) => listeners.add(listener),
    removeEventListener: (
      _type: string,
      listener: (event: MediaQueryListEvent) => void,
    ) => listeners.delete(listener),
  } as unknown as MediaQueryList;
  vi.stubGlobal("matchMedia", vi.fn().mockReturnValue(list));
  return {
    change(next: boolean) {
      current = next;
      listeners.forEach((listener) =>
        listener({ matches: next } as MediaQueryListEvent),
      );
    },
  };
}

describe("app-theme", () => {
  afterEach(() => {
    localStorage.removeItem(APP_THEME_STORAGE_KEY);
    document.documentElement.removeAttribute("data-mm-theme");
    vi.unstubAllGlobals();
  });

  it("parses only the two stored values, otherwise leaves the choice open", () => {
    expect(parseAppTheme(null)).toBeNull();
    expect(parseAppTheme("")).toBeNull();
    expect(parseAppTheme("nope")).toBeNull();
    expect(parseAppTheme("dark")).toBe("dark");
    expect(parseAppTheme("light")).toBe("light");
  });

  it("reads null from localStorage until someone has picked a theme", () => {
    localStorage.setItem(APP_THEME_STORAGE_KEY, "light");
    expect(readStoredAppTheme()).toBe("light");
    localStorage.removeItem(APP_THEME_STORAGE_KEY);
    expect(readStoredAppTheme()).toBeNull();
  });

  it("applies and persists the document theme", () => {
    applyAppThemeToDocument("light");
    expect(document.documentElement.getAttribute("data-mm-theme")).toBe(
      "light",
    );
    persistAppTheme("dark");
    expect(localStorage.getItem(APP_THEME_STORAGE_KEY)).toBe("dark");
    expect(document.documentElement.getAttribute("data-mm-theme")).toBe("dark");
  });

  it("follows the system's light or dark setting until someone picks one", () => {
    stubSystemPrefersLight(false);
    expect(currentAppTheme()).toBe("dark");

    stubSystemPrefersLight(true);
    expect(currentAppTheme()).toBe("light");
  });

  it("keeps a picked theme over the system setting", () => {
    stubSystemPrefersLight(true);
    persistAppTheme("dark");

    expect(currentAppTheme()).toBe("dark");
  });

  it("re-applies the document theme when the system setting changes", () => {
    const system = stubSystemPrefersLight(false);
    const stop = followSystemAppTheme();

    system.change(true);

    expect(document.documentElement.getAttribute("data-mm-theme")).toBe(
      "light",
    );
    stop();
  });

  it("stops following once someone picks a theme", () => {
    const system = stubSystemPrefersLight(false);
    const stop = followSystemAppTheme();

    persistAppTheme("dark");
    system.change(true);

    expect(document.documentElement.getAttribute("data-mm-theme")).toBe("dark");
    stop();
  });

  it("switches an already-open tab when another tab changes the stored theme", () => {
    const stop = followSystemAppTheme();
    localStorage.setItem(APP_THEME_STORAGE_KEY, "light");

    window.dispatchEvent(
      new StorageEvent("storage", {
        key: APP_THEME_STORAGE_KEY,
        newValue: "light",
      }),
    );

    expect(document.documentElement.getAttribute("data-mm-theme")).toBe(
      "light",
    );
    stop();
  });

  it("ignores a storage event for an unrelated key", () => {
    applyAppThemeToDocument("dark");
    const stop = followSystemAppTheme();
    localStorage.setItem("some-other-key", "light");

    window.dispatchEvent(
      new StorageEvent("storage", { key: "some-other-key", newValue: "light" }),
    );

    expect(document.documentElement.getAttribute("data-mm-theme")).toBe("dark");
    stop();
  });
});

describe("when the browser will not let the theme be stored", () => {
  afterEach(() => vi.restoreAllMocks());

  const blocked = () => new DOMException("blocked", "SecurityError");

  it("reads no choice, as if nobody had picked one", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw blocked();
    });

    expect(readStoredAppTheme()).toBeNull();
  });

  it("still changes the theme for this visit", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw blocked();
    });

    persistAppTheme("light");

    expect(document.documentElement.getAttribute("data-mm-theme")).toBe(
      "light",
    );
  });

  it("does not hide a failure that is not the browser refusing", () => {
    const broken = new TypeError("not storage");
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw broken;
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw broken;
    });

    expect(() => readStoredAppTheme()).toThrow(broken);
    expect(() => persistAppTheme("dark")).toThrow(broken);
  });
});
