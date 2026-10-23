import { render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import type { LibraryForm } from "./library-form";
import { SelectSetting } from "./library-settings";

const binding = {
  form: { rejected_file_action: "delete" } as unknown as LibraryForm,
  update: vi.fn(),
  editable: true,
};

it("gives only the reason for a choice that is held, when the setting has no hint of its own", () => {
  render(
    <SelectSetting
      binding={binding}
      name="rejected_file_action"
      label="When a file is rejected"
      options={[{ value: "leave", label: "Leave it where it is" }]}
      lockedTo={{ value: "leave", reason: "Weir never deletes this file." }}
    />,
  );

  expect(screen.getByTestId("rejected_file_action-locked")).toHaveTextContent(
    "Weir never deletes this file.",
  );
  expect(screen.getByRole("combobox")).toBeDisabled();
  expect(screen.getByRole("combobox")).toHaveValue("leave");
});
