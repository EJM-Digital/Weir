import { renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import {
  APP_THEME_STORAGE_KEY,
  persistAppTheme,
  readStoredAppTheme,
} from "./app-theme";
import { useAccountThemeSync } from "./use-account-theme-sync";

afterEach(() => {
  localStorage.removeItem(APP_THEME_STORAGE_KEY);
  document.documentElement.removeAttribute("data-mm-theme");
});

it("keeps a local choice and adopts it onto an account with no preference, saving it once", () => {
  persistAppTheme("dark");
  const onAdopt = vi.fn();

  const { rerender } = renderHook(
    ({ theme }) => useAccountThemeSync(theme, onAdopt),
    { initialProps: { theme: null as "dark" | "light" | null | undefined } },
  );
  // A second /auth/me answer while the save is still in flight must not adopt again.
  rerender({ theme: undefined });
  rerender({ theme: null });

  expect(readStoredAppTheme()).toBe("dark");
  expect(onAdopt).toHaveBeenCalledTimes(1);
  expect(onAdopt).toHaveBeenCalledWith("dark");
});

it("follows the system and saves nothing when neither the account nor this browser has a preference", () => {
  const onAdopt = vi.fn();

  renderHook(({ theme }) => useAccountThemeSync(theme, onAdopt), {
    initialProps: { theme: null as "dark" | "light" | null | undefined },
  });

  expect(readStoredAppTheme()).toBeNull();
  expect(onAdopt).not.toHaveBeenCalled();
});

it("lets the account's own preference override a different local choice", () => {
  persistAppTheme("dark");
  const onAdopt = vi.fn();

  renderHook(({ theme }) => useAccountThemeSync(theme, onAdopt), {
    initialProps: { theme: "light" as "dark" | "light" | null | undefined },
  });

  expect(readStoredAppTheme()).toBe("light");
  expect(document.documentElement.getAttribute("data-mm-theme")).toBe("light");
  expect(onAdopt).not.toHaveBeenCalled();
});

it("leaves the first-paint cache alone while the account is still unknown", () => {
  persistAppTheme("dark");
  const onAdopt = vi.fn();

  renderHook(({ theme }) => useAccountThemeSync(theme, onAdopt), {
    initialProps: { theme: undefined },
  });

  expect(readStoredAppTheme()).toBe("dark");
  expect(onAdopt).not.toHaveBeenCalled();
});

it("applies the new theme again once the account's own preference arrives", () => {
  const onAdopt = vi.fn();
  const { rerender } = renderHook(
    ({ theme }) => useAccountThemeSync(theme, onAdopt),
    { initialProps: { theme: "dark" as "dark" | "light" | null | undefined } },
  );
  expect(readStoredAppTheme()).toBe("dark");

  rerender({ theme: "light" });

  expect(readStoredAppTheme()).toBe("light");
});
