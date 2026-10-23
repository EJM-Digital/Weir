import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { useLibrarySetupDraft, type SetupDraft } from "./library-setup-draft";

const mocks = vi.hoisted(() => ({
  saveSettings: vi.fn(),
  setSchedule: vi.fn(),
}));

vi.mock("../../lib/processing/library-mode-queries", () => ({
  useSaveLibrarySettings: () => ({ mutateAsync: mocks.saveSettings }),
  useSetLibrarySchedule: () => ({ mutateAsync: mocks.setSchedule }),
}));

const saved = {
  library_folders: ["D:/tv"],
  clean_hardlinked_files: false,
  skip_if_manager_would_redownload: true,
  keep_original_after_clean: false,
  originals_folder: "",
  library_rule_set_id: null,
  library_schedule_enabled: false,
} as SetupDraft;

describe("saving a library's setup", () => {
  it("sends nothing when nothing has been changed", async () => {
    const { result } = renderHook(() => useLibrarySetupDraft(1, saved));

    await act(async () => result.current.save());

    expect(mocks.saveSettings).not.toHaveBeenCalled();
    expect(mocks.setSchedule).not.toHaveBeenCalled();
  });

  it("sends the settings that were changed", async () => {
    const { result } = renderHook(() => useLibrarySetupDraft(1, saved));
    act(() => result.current.change({ clean_hardlinked_files: true }));

    await act(async () => result.current.save());

    expect(mocks.saveSettings).toHaveBeenCalledWith(
      expect.objectContaining({ clean_hardlinked_files: true }),
    );
    expect(mocks.setSchedule).not.toHaveBeenCalled();
  });
});
