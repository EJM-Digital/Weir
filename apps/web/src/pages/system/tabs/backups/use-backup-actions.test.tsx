import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import { useBackupActions } from "./use-backup-actions";

const mocks = vi.hoisted(() => ({ putConfigurationBundle: vi.fn() }));

vi.mock("../../../../lib/settings/settings-api", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/settings/settings-api")
  >()),
  putConfigurationBundle: mocks.putConfigurationBundle,
}));

function wrapper({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={new QueryClient()}>
      {children}
    </QueryClientProvider>
  );
}

describe("confirming a restore", () => {
  it("replaces nothing when no backup is being held for confirmation", () => {
    const { result } = renderHook(() => useBackupActions(), { wrapper });

    act(() => result.current.confirmRestore("a password"));

    expect(mocks.putConfigurationBundle).not.toHaveBeenCalled();
  });
});
